open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open FakeAzureOcrApi.Handlers

let builder = WebApplication.CreateBuilder()
let app = builder.Build()

// Azure Computer Vision (stream) analyze endpoint used by AzureOcrService:
// POST /computervision/imageanalysis:analyze?overload=stream&api-version=2024-02-01&features=read
app.MapPost("/computervision/imageanalysis:analyze", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleAnalyze ctx))
|> ignore

// Azure OpenAI Chat Completions (used by VahterBanBot LLM triage)
app.MapPost("/openai/deployments/{deployment}/chat/completions", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleChatCompletions ctx))
|> ignore

// Azure OpenAI audio/transcriptions (used by AlitaBot voice transcription)
app.MapPost("/openai/deployments/{deployment}/audio/transcriptions", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleAudioTranscriptions ctx))
|> ignore

// Azure OpenAI images/generations + images/edits (used by AlitaBot image generation, S3)
app.MapPost("/openai/deployments/{deployment}/images/generations", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleImagesGenerations ctx))
|> ignore
app.MapPost("/openai/deployments/{deployment}/images/edits", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleImagesEdits ctx))
|> ignore

// Azure OpenAI embeddings (used by AlitaBot's memory foundation, Slice 5a)
app.MapPost("/openai/deployments/{deployment}/embeddings", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleEmbeddings ctx))
|> ignore

// Test endpoints (configure response / inspect calls)
app.MapPost("/test/mock/reset",     Func<HttpContext, Threading.Tasks.Task>(fun ctx -> resetMock ctx))    |> ignore
app.MapPost("/test/mock/response",  Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setResponse ctx))  |> ignore
app.MapPost("/test/mock/delay",     Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setDelay ctx))     |> ignore
app.MapPost("/test/mock/errorMode", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setErrorMode ctx)) |> ignore
app.MapPost("/test/mock/script",    Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setScript ctx))    |> ignore
app.MapPost("/test/mock/llm-script", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setLlmScript ctx)) |> ignore
app.MapPost("/test/mock/reaction-llm-script", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setReactionLlmScript ctx)) |> ignore
app.MapPost("/test/mock/azure-llm-stream-options", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setLlmStreamOptions ctx)) |> ignore
app.MapPost("/test/mock/stt-script", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setSttScript ctx)) |> ignore
app.MapPost("/test/mock/image-script", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setImageScript ctx)) |> ignore
app.MapPost("/test/mock/embeddings-script", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setEmbeddingsScript ctx)) |> ignore
app.MapGet("/test/calls", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> getCalls ctx)) |> ignore
app.MapDelete("/test/calls", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> clearCalls ctx)) |> ignore

app.MapGet("/health", Func<string>(fun () -> "OK")) |> ignore

app.Run()

