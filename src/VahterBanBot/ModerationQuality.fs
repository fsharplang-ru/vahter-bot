namespace VahterBanBot

open System
open BotInfra
open VahterBanBot.Types

[<RequireQualifiedAccess>]
type QualityOutcome =
    | TruePositive | FalsePositive | FalseNegative | TrueNegative | Abstained | Unresolved | Excluded
    member this.Code =
        match this with
        | QualityOutcome.TruePositive -> "tp"
        | QualityOutcome.FalsePositive -> "fp"
        | QualityOutcome.FalseNegative -> "fn"
        | QualityOutcome.TrueNegative -> "tn"
        | QualityOutcome.Abstained -> "abstained"
        | QualityOutcome.Unresolved -> "unresolved"
        | QualityOutcome.Excluded -> "excluded"

type QualityContribution =
    { Cohort: string
      System: string
      LlmModel: string option
      Outcome: QualityOutcome }

type QualityEvaluation = { EvaluatedAt: DateTime; Contributions: QualityContribution list }

type QualityUnban = { At: DateTime; Id: int64; ChatId: int64; MessageId: int64 }

module ModerationQuality =
    let private key (row: RawEvent) = row.created_at, row.id

    let unbans (store: EventStore) (rows: RawEvent list) =
        let mutable target = None
        [ for row in List.sortBy key rows do
            match store.Deserialize<UserEvent> row with
            | UserBanned e ->
                target <-
                    match e.chatId, e.messageId, e.bannedBy with
                    | Some chat, Some msg, _ -> Some (chat, msg)
                    | _, _, Some (BannedByVahter b) -> Some (b.chatId, int64 b.messageId)
                    | _, _, Some (BannedByAI b) -> Some (b.chatId, int64 b.messageId)
                    | _ -> None
            | UserUnbanned e ->
                match e.actor, e.unbannedBy, target with
                | Some (Actor.User _), _, Some (chat, msg)
                | None, Some _, Some (chat, msg) when chat <> 0L && msg <> 0L ->
                    yield { At = row.created_at; Id = row.id; ChatId = chat; MessageId = msg }
                | _ -> ()
            | _ -> () ]

    let private classify prediction truth =
        match prediction, truth with
        | true, Some SpamClassification.Spam -> QualityOutcome.TruePositive
        | true, Some SpamClassification.Ham -> QualityOutcome.FalsePositive
        | false, Some SpamClassification.Spam -> QualityOutcome.FalseNegative
        | false, Some SpamClassification.Ham -> QualityOutcome.TrueNegative
        | _ -> QualityOutcome.Unresolved

    let evaluate (store: EventStore) (message: RawEvent list) (moderation: RawEvent list)
                 (detection: RawEvent list) (corrections: QualityUnban list) =
        let scores = detection |> List.filter (fun r -> r.event_type = "MlScoredMessage") |> List.sortBy key
        let labels =
            [ for row in message do
                match store.Deserialize<MessageEvent> row with
                | MessageMarkedHam _ -> yield key row, SpamClassification.Ham
                | MessageMarkedSpam _ -> yield key row, SpamClassification.Spam
                | _ -> ()
              for row in moderation do
                match store.Deserialize<ModerationEvent> row with
                | VahterActed e ->
                    match e.actionType with
                    | PotentialKill | ManualBan -> yield key row, SpamClassification.Spam
                    | PotentialNotSpam | DetectedNotSpam -> yield key row, SpamClassification.Ham
                    | _ -> ()
                | _ -> ()
              for c in corrections do yield (c.At, c.Id), SpamClassification.Ham ]
        let human = labels |> List.sortBy fst |> List.tryLast
        let beforeReview row = human |> Option.forall (fun (at, _) -> key row < at)
        let scoreValue row =
            match store.Deserialize<DetectionEvent> row with
            | MlScoredMessage e -> e.score
            | _ -> invalidArg (nameof row) "Expected ML score"
        let changedSince at =
            let content limit =
                message
                |> List.filter (fun r -> key r <= limit && (r.event_type = "MessageReceived" || r.event_type = "MessageEdited"))
                |> List.sortBy key |> List.tryLast
                |> Option.bind (fun r ->
                    match store.Deserialize<MessageEvent> r with
                    | MessageReceived e -> e.text
                    | MessageEdited e -> e.text
                    | _ -> None)
            human |> Option.exists (fun (reviewAt, _) -> content reviewAt <> content at)
        match List.tryHead scores, scores |> List.filter beforeReview |> List.tryLast with
        | None, _ -> None
        | Some first, latest ->
            let llm =
                detection |> List.filter (fun r -> beforeReview r && (r.event_type = "LlmClassified" || r.event_type = "LlmVerdictCacheHit"))
                |> List.sortBy key |> List.tryLast
                |> Option.bind (fun r ->
                    match store.Deserialize<DetectionEvent> r with
                    | LlmClassified e -> Some (r, e.verdict, e.modelName)
                    | LlmVerdictCacheHit e -> Some (r, e.verdict, e.modelName)
                    | _ -> None)
            let deletion =
                moderation |> List.filter (fun r -> r.event_type = "BotAutoDeleted" && beforeReview r)
                |> List.sortBy key |> List.tryLast
                |> Option.map (fun r ->
                    match store.Deserialize<ModerationEvent> r with
                    | BotAutoDeleted e -> e.reason
                    | _ -> invalidArg (nameof r) "Expected automatic deletion")
            let automated, excluded =
                match deletion with
                | Some (MlSpam _ | LlmSpam _) -> true, false
                | Some _ -> false, true
                | None -> false, false
            let llmTruth verdict =
                match verdict with
                | "SPAM" | "KILL" -> Some SpamClassification.Spam
                | "NOT_SPAM" -> Some SpamClassification.Ham
                | _ -> None
            let truth =
                match human with
                | Some (_, label) -> Some label
                | None when automated -> Some SpamClassification.Spam
                | None ->
                    match latest, llm with
                    | Some ml, Some (r, verdict, _) when key r >= key ml -> llmTruth verdict
                    | _ -> Some SpamClassification.Ham
            let all system prediction =
                { Cohort = "all_scored"; System = system; LlmModel = None
                  Outcome =
                    match latest with
                    | None -> QualityOutcome.Excluded
                    | Some ml when excluded || changedSince (key ml) -> QualityOutcome.Excluded
                    | Some _ -> classify prediction truth }
            let contributions =
                [ yield all "pipeline" automated
                  yield all "ml" (latest |> Option.exists (fun r -> scoreValue r > 0.0))
                  match llm with
                  | Some (r, verdict, model) ->
                      match scores |> List.filter (fun s -> key s < key r) |> List.tryLast with
                      | Some ml when scoreValue ml >= -0.5 && scoreValue ml < 1.5 ->
                          let reference = human |> Option.map snd |> Option.orElseWith (fun () -> llmTruth verdict)
                          let result prediction =
                              match changedSince (key r), verdict with
                              | true, _ -> QualityOutcome.Excluded
                              | false, "SKIP" -> QualityOutcome.Abstained
                              | _ -> classify prediction reference
                          yield { Cohort = "llm_triaged"; System = "ml"; LlmModel = model; Outcome = result (scoreValue ml > 0.0) }
                          yield { Cohort = "llm_triaged"; System = "llm"; LlmModel = model; Outcome = result (verdict = "SPAM" || verdict = "KILL") }
                      | _ -> ()
                  | None -> () ]
            Some { EvaluatedAt = first.created_at; Contributions = contributions }
