module VahterBanBot.LlmTriagePipeline

open System
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open VahterBanBot.LlmLayerConfiguration
open VahterBanBot.Types
open VahterBanBot.Utils
open VahterBanBot.Telemetry
open BotInfra
open BotInfra.JsonSetup

/// Frozen evidence, including nonce fences; layer outputs never enter another layer's input.
type LlmTriageContext =
    { ChatDescription: string
      UserPrompt: string }

type LlmLayerResult =
    { Verdict: LlmVerdict
      PromptTokens: int
      CompletionTokens: int
      LatencyMs: int }

/// One layer bound to a settings snapshot; CacheIdentity covers its model, prompt and acceptance gate.
type ConfiguredLlmLayer =
    { Id: LlmLayerId
      Source: LlmTriageSource
      CacheIdentity: string
      Classify: LlmTriageContext * CancellationToken -> Task<LlmLayerResult> }

type ILlmTriageLayer =
    abstract member Id: LlmLayerId
    abstract member Configure: BotConfiguration -> ConfiguredLlmLayer

type LlmTriageResult =
    { Verdict: LlmVerdict
      Source: LlmTriageSource option }

type LlmPipelineResult =
    { Classification: LlmLayerResult
      Source: LlmTriageSource option }

let pipelineCacheIdentity (conf: BotConfiguration) (layers: ConfiguredLlmLayer list) =
    let policies = conf.LlmChatDescriptions |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.sortBy fst |> Seq.toList
    let identity =
        toJson ("triage-context-v1", layers |> List.map (fun layer -> layer.Id.SettingValue, layer.CacheIdentity), policies, conf.LlmContentFilterIsSpam)
    SHA256.HashData(Encoding.UTF8.GetBytes identity) |> Convert.ToHexString

let runLayers (logger: ILogger) contentFilterIsSpam (layers: ConfiguredLlmLayer list) context (ct: CancellationToken) =
    let rec run verdictSoFar remaining = task {
        match remaining with
        | [] ->
            match verdictSoFar with
            | None ->
                logger.LogWarning("LLM triage has no configured layers")
                return { Classification = { Verdict = LlmVerdict.Error; PromptTokens = 0; CompletionTokens = 0; LatencyMs = 0 }
                         Source = None }
            | Some previous ->
                match previous.Classification.Verdict with
                | LlmVerdict.ContentFiltered _ ->
                    return { previous with Classification = { previous.Classification with Verdict = LlmVerdict.Error } }
                | _ -> return previous
        | layer :: tail ->
            use activity = botActivity.StartActivity("llmTriage.route")
            let sw = Stopwatch.StartNew()
            let! result = task {
                try
                    ct.ThrowIfCancellationRequested()
                    return! layer.Classify(context, ct)
                with ex ->
                    logger.LogWarning(ex, "LLM layer {Layer} failed", layer.Id)
                    return { Verdict = LlmVerdict.Error; PromptTokens = 0; CompletionTokens = 0; LatencyMs = int sw.ElapsedMilliseconds }
            }
            let accepted =
                match result.Verdict with
                | LlmVerdict.Kill _ | LlmVerdict.NotSpam _ -> true
                | LlmVerdict.ContentFiltered _ -> contentFilterIsSpam
                | LlmVerdict.Skip _ | LlmVerdict.Error -> false
            if not (isNull activity) then
                %activity.SetTag("llm.layer", layer.Id.SettingValue).SetTag("llm.model", layer.Source.ModelName)
                    .SetTag("llm.accepted", accepted).SetTag("latency_ms", sw.ElapsedMilliseconds)
                    .SetTag("llm.prompt_hash", layer.Source.PromptHash)
                    .SetTag("llm.input_tokens", result.PromptTokens).SetTag("llm.output_tokens", result.CompletionTokens)
            logger.LogInformation("LLM layer {Layer} model={Model} verdict={Verdict} accepted={Accepted} latencyMs={LatencyMs}",
                                  layer.Id, layer.Source.ModelName, result.Verdict, accepted, sw.ElapsedMilliseconds)
            let current = { Classification = result; Source = Some layer.Source }
            if ct.IsCancellationRequested then
                return { current with Classification = { result with Verdict = LlmVerdict.Error } }
            elif accepted then
                return current
            else
                return! run (Some current) tail
    }
    run None layers
