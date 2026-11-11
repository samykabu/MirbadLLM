namespace MirbadLLM.Web;

/// <summary>What a field's info panel shows: what it is, why it matters, and a concrete example from this deployment.</summary>
public sealed record FieldHelpText(string What, string Why, string Example);

/// <summary>
/// Every field-level help text shown by the "add model" editor's info (ⓘ) buttons, in one place so the
/// wording is reviewable and testable. Facts (parser choices, memory numbers, cudagraph behaviour) come from
/// <c>docs/phase0-results.md</c> — real measurements on this DGX Spark, not vLLM documentation in general.
/// </summary>
public static class ModelFieldHelp
{
    // --- Source ---

    public static readonly FieldHelpText SourceMode = new(
        What: "Where the checkpoint files come from: a folder already on the node, or a fresh download from Hugging Face.",
        Why: "The runtime needs the full checkpoint (weights, config.json, tokenizer files) sitting under the models root " +
             "before it can serve it. Picking a folder skips straight to configuring the model; downloading fetches " +
             "the files first and then lets you configure it.",
        Example: "The three checkpoints already on this box (Ornith-1.5-9B-uncensored and its two NVFP4 variants) show up " +
                 "under \"Folder already on the DGX\". A new model, e.g. a fresh Qwen3 release, would use \"Download from Hugging Face\".");

    public static readonly FieldHelpText RepoId = new(
        What: "The Hugging Face repository id: \"{owner}/{name}\".",
        Why: "This is what the download's snapshot_download() call fetches from huggingface.co. A wrong id fails fast " +
             "with a 404 before any bytes are pulled.",
        Example: "ornith-ai/Ornith-1.5-9B — the official (non-uncensored) NVFP4 release referenced in docs/phase0-results.md.");

    public static readonly FieldHelpText Revision = new(
        What: "Git branch, tag or commit SHA within the repo. Leave blank for the repo's default branch.",
        Why: "Some repos publish quantized variants or fixes on a branch other than main; pinning a revision makes the " +
             "download reproducible even if the repo changes later.",
        Example: "Leave blank for most repos. A value like \"refs/pr/3\" would fetch an open pull request's files instead.");

    public static readonly FieldHelpText Token = new(
        What: "A saved Hugging Face access token (read scope is enough), picked by name. Save tokens under Access > Tokens.",
        Why: "Gated or private repos return 401/403 without a token that has been granted access. The master reads the value " +
             "only as the download starts and sends it as the HF_TOKEN environment variable to the download — never on the " +
             "command line, never logged and never sent to the browser.",
        Example: "Needed for a gated repo such as meta-llama/Llama-3.1-8B-Instruct. Not needed for the Ornith checkpoints, which are public.");

    public static readonly FieldHelpText Folder = new(
        What: "The destination folder name under the models root (mounted at /models in every container).",
        Why: "This is the folder the runtime loads, so it must be unique and match " +
             "exactly. Left blank, it defaults to the part of the repo id after the last '/'.",
        Example: "Repo ornith-ai/Ornith-1.5-9B with a blank folder downloads into /models/Ornith-1.5-9B.");

    public static readonly FieldHelpText GgufVariant = new(
        What: "Which GGUF quantization to fetch. A GGUF repo usually publishes several — this picks exactly one, " +
             "keeping any split files (…-00001-of-00003.gguf etc.) that make it up together.",
        Why: "Downloading every quantization in a GGUF repo would pull hundreds of gigabytes you don't need. " +
             "Picking a variant sets Allow patterns to just that variant's files, plus config/tokenizer files if the repo has any.",
        Example: "Q4_K_M — 3 files, 119.2 GiB is a common middle-ground quantization; Q8_0 is closer to full quality " +
                 "and larger, IQ2_XXS is smaller and lower quality. Check the size against the memory shown next to it before picking one.");

    public static readonly FieldHelpText AllowPatterns = new(
        What: "Glob patterns of files to fetch from the repo (comma-separated); everything else in the repo is skipped.",
        Why: "Most repos also carry files the model doesn't need (README, sample images, alternate formats like GGUF or ONNX). " +
             "Restricting the download to weights, config and tokenizer files avoids pulling gigabytes you don't need.",
        Example: "The default — *.json, *.safetensors, *.jinja, *.txt, tokenizer*, *.model — is exactly what the three " +
                 "Ornith checkpoints on this box consist of.");

    // --- Identity ---

    public static readonly FieldHelpText Id = new(
        What: "A stable slug for this model: lowercase letters, digits and hyphens, starting with a letter or digit, max 40 characters.",
        Why: "The controller names the container ss-{id} and uses {id} as the registry key. It can't be changed after " +
             "creation without deleting and re-adding the model.",
        Example: "qwen-coder-7b, whose container is then named ss-qwen-coder-7b.");

    public static readonly FieldHelpText ServedName = new(
        What: "The name clients put in the OpenAI \"model\" field of a chat request.",
        Why: "The router matches incoming requests to an upstream by this exact name (case-insensitive), and it is " +
             "passed to the server so /v1/models advertises it too. Two models can't share one.",
        Example: "Qwen-Coder-7B, the name VS Code or Cline then shows in its model picker.");

    public static readonly FieldHelpText ModelPath = new(
        What: "The checkpoint's folder name, relative to the models root (bound read-only at /models in the container).",
        Why: "The runtime loads this folder. It must be the exact folder name containing " +
             "config.json and the *.safetensors weight files.",
        Example: "Ornith-1.5-9B-uncensored, or Ornith-1.5-9B-NVFP4 for the pre-quantized variant.");

    // --- Precision / memory ---

    public static readonly FieldHelpText Quantization = new(
        What: "Online quantization vLLM applies while loading full-precision weights. \"None\" means the checkpoint " +
              "is already quantized (compressed-tensors/ModelOpt NVFP4, FP8, etc.) and needs no conversion at load time.",
        Why: "Quantizing at load time trades a little extra load time and (usually) a little quality for roughly half " +
             "the weight memory of BF16 — the difference between 18 GiB and ~9 GiB of weights for this 9B model.",
        Example: "fp8 for the uncensored BF16 checkpoint (Phase 0: 23.4 -> 326.0 tok/s from N=1 to N=16). Pre-quantized " +
                 "NVFP4 checkpoints use \"None\" here — they're already compressed on disk.");

    public static readonly FieldHelpText KvCacheDtype = new(
        What: "Storage format for the attention KV cache: auto (same dtype as the weights) or fp8.",
        Why: "The KV cache, not the weights, is usually what limits how much context you can serve concurrently. " +
             "fp8 halves that memory, roughly doubling how many tokens of context fit in the same budget.",
        Example: "fp8 on every Phase 0 model — it's what turns a 36 GiB budget into ~1.45M tokens of KV cache for the 9B model.");

    public static readonly FieldHelpText MemoryBudgetGiB = new(
        What: "Unified memory reserved for this model's container: weights + runtime overhead + KV cache, in GiB.",
        Why: "Converted to --gpu-memory-utilization (this value / total unified memory) since the DGX Spark's CPU and " +
             "GPU share one memory pool. Starting a model that would push used+reserve past what's available is refused " +
             "unless you force it.",
        Example: "36 GiB of the box's 121.7 GiB total -> --gpu-memory-utilization 0.3000 (4 decimal places). The suggested " +
                 "value shown when you pick a folder uses: weights GiB + 7 GiB runtime overhead (measured on this box) + KV cache " +
                 "for up to 131072 tokens at fp8, rounded up to a whole GiB. The budget must hold at least one request of " +
                 "Max model len tokens, or vLLM refuses to start: 262,144 tokens on the 9B model needs about 20 GiB.");

    // --- Context / concurrency ---

    public static readonly FieldHelpText MaxModelLen = new(
        What: "Maximum context length (prompt + generation) in tokens that this model instance will accept.",
        Why: "Bounds how much KV cache memory a single request can use; requests longer than this are rejected by vLLM " +
             "rather than silently truncated. Presets are filtered to the checkpoint's own max_position_embeddings.",
        Example: "262144 (256K) is the checkpoint's own limit and what Phase 0 tested up to (137 s cold prefill, 1.2 s with a prefix-cache hit).");

    public static readonly FieldHelpText MaxNumSeqs = new(
        What: "Maximum number of requests vLLM will batch and run concurrently.",
        Why: "Raising it increases throughput (more requests share the same forward pass) up to a point, but each " +
             "concurrent sequence needs its own slice of the KV cache budget.",
        Example: "16 was the concurrency Phase 0 measured aggregate throughput at: 326 tok/s (FP8) and 485 tok/s (NVFP4) with 256-token outputs.");

    public static readonly FieldHelpText MaxNumBatchedTokens = new(
        What: "vLLM --max-num-batched-tokens: how many tokens one scheduler step may process, which is also the chunk size for long prompts (chunked prefill).",
        Why: "A larger budget prefills long prompts in fewer, bigger steps (faster time to first token) but makes decode " +
             "steps of other requests wait longer behind them; a smaller one keeps streaming smooth under load. " +
             "Default lets vLLM pick for this model.",
        Example: "Tuning tries 2,048, 8,192 and 16,384 on this box and keeps whichever scores best for the chosen goal.");

    // --- Tool/reasoning parsing ---

    public static readonly FieldHelpText ToolCallParser = new(
        What: "Which vLLM parser extracts OpenAI-style tool calls from this model's raw output.",
        Why: "Different model families emit tool calls in different textual formats (JSON blocks, XML tags, Python-call " +
             "syntax); the parser has to match the family or tool calls won't be recognized at all.",
        Example: "qwen3_coder — Qwen3.5/Qwen3-Next/Qwen3 models emit XML-style <tool_call><function=...> calls, which is " +
                 "what this parser expects. Phase 0 confirmed it works even with VS Code's schema-without-properties case.");

    public static readonly FieldHelpText ReasoningParser = new(
        What: "Which vLLM parser separates this model's \"thinking\" text from its final answer.",
        Why: "Reasoning models wrap chain-of-thought in a family-specific delimiter; the right parser strips it into a " +
             "separate reasoning field instead of leaving it mixed into the visible reply.",
        Example: "qwen3 for every Qwen3/Qwen3.5 model on this box. Leave both parsers unset for a model whose family isn't in the list.");

    // --- Performance features ---

    public static readonly FieldHelpText EnablePrefixCaching = new(
        What: "Reuses the KV cache of a previously seen prompt prefix instead of recomputing it. On passes " +
              "--enable-prefix-caching, Off passes --no-enable-prefix-caching, vLLM default passes neither.",
        Why: "Huge win for repeated system prompts or long shared context (agent loops, RAG). Phase 0 measured a " +
             "242K-token prompt drop from 137 s cold prefill to 1.2 s on a prefix-cache hit.",
        Example: "Enabled for every Phase 0 model; there's essentially no reason to turn it off on this workload.");

    public static readonly FieldHelpText SpeculativeEnabled = new(
        What: "Speculative decoding: a cheap draft step proposes several tokens, the model verifies them in one pass.",
        Why: "Can raise single-stream throughput noticeably when the draft's guesses are often right, at the cost of " +
             "extra GPU work per verification step that doesn't pay off when the draft is wrong a lot.",
        Example: "MTP on the official NVFP4 checkpoint: 48.1 tok/s at N=1 vs 41.0 without it (matching acceptance ~1.76 on story prompts). " +
                 "The uncensored checkpoint has no MTP weights, so speculative decoding isn't available for it.");

    public static readonly FieldHelpText SpeculativeMethod = new(
        What: "The speculative-decoding algorithm: mtp (needs MTP weights baked into the checkpoint) or ngram (prompt lookup, no extra weights).",
        Why: "mtp only works if the checkpoint was trained/grafted with MTP layers; ngram works on any checkpoint but " +
             "only helps when the output repeats text already in the prompt.",
        Example: "mtp for Ornith-1.5-9B-NVFP4 (num_speculative_tokens 2). The uncensored checkpoint would need ngram instead, since it has no MTP weights.");

    public static readonly FieldHelpText SpeculativeDraftModel = new(
        What: "Path of a separate draft checkpoint (the speculative config's model key), as the container sees it under /models.",
        Why: "Draft-model methods (e.g. a DSpark or DFlash drafter) propose tokens with a small trained model instead of " +
             "MTP layers; vLLM infers the method from the drafter when Method is left empty.",
        Example: "/models/Nemotron-3.5-Lightning-30B-A3B-NVFP4-DSpark with 3 speculative tokens for nemotron-3-5.");

    public static readonly FieldHelpText SglangSpeculativeEnabled = new(
        What: "SGLang speculative decoding: a cheap draft step proposes several tokens, the model verifies them in one pass.",
        Why: "Can raise single-stream throughput when the draft's guesses are often right. Whether it works depends on the SGLang image and the drafter, and only a start shows that.",
        Example: "DFLASH with a DFlash draft checkpoint downloaded under Extra checkpoints.");

    public static readonly FieldHelpText SglangSpeculativeAlgorithm = new(
        What: "The value of SGLang's --speculative-algorithm flag.",
        Why: "Each algorithm needs a matching draft: DFLASH a DFlash checkpoint, EAGLE and EAGLE3 an EAGLE checkpoint, STANDALONE a small ordinary model. NGRAM needs none.",
        Example: "DFLASH with z-lab's DFlash checkpoint for your model.");

    public static readonly FieldHelpText SglangSpeculativeDepth = new(
        What: "How much to draft per step: --speculative-num-draft-tokens for DFLASH and NGRAM, --speculative-num-steps for EAGLE, EAGLE3 and STANDALONE.",
        Why: "Too deep wastes GPU work when guesses miss; a drafter's block size also limits it.",
        Example: "16 for DFLASH (SGLang's default block size), 3 for EAGLE3.");

    public static readonly FieldHelpText SglangSpeculativeDraftModel = new(
        What: "Path of the draft checkpoint as the container sees it under /models. Leave it empty for NGRAM.",
        Why: "Add the same folder under Extra checkpoints so a move or restore can download it; a move is refused without it.",
        Example: "/models/my-model-DFlash");

    public static readonly FieldHelpText SpeculativeExtra = new(
        What: "Any other --speculative-config key, as a JSON object; kept verbatim and merged with the fields above.",
        Why: "vLLM's speculative config has many more knobs than this form; they round-trip here instead of hiding in the extra args.",
        Example: "{\"moe_backend\":\"triton\"} — what the 35B's MTP config uses.");

    public static readonly FieldHelpText AsyncScheduling = new(
        What: "vLLM's asynchronous scheduler: On passes --async-scheduling, Off --no-async-scheduling, vLLM default neither.",
        Why: "Overlaps scheduling with the GPU step, which NVIDIA's DGX Spark recipes use; whether it helps depends on the model.",
        Example: "On for qwen36-35b-a3b-nvfp4 (it used to be a raw --async-scheduling extra arg).");

    public static readonly FieldHelpText MambaCacheMode = new(
        What: "--mamba-cache-mode for hybrid (Mamba / linear-attention) models: how the recurrent state is cached.",
        Why: "Prefix caching on a hybrid model needs the state cached at block boundaries (align); none saves memory when prefix caching is off.",
        Example: "align for nemotron-3-5 with prefix caching on.");

    public static readonly FieldHelpText CompilationConfig = new(
        What: "Any other --compilation-config key besides the CUDA graph mode, as a JSON object; merged with it into one argument.",
        Why: "Keeps advanced compilation settings in the definition instead of a raw -cc/--compilation-config extra arg that would override the graph mode.",
        Example: "Leave empty unless a recipe asks for e.g. {\"pass_config\":{\"enable_fusion\":true}}.");

    public static readonly FieldHelpText SpeculativeNumTokens = new(
        What: "How many tokens the draft step proposes ahead per verification pass.",
        Why: "Higher values raise the potential speedup but also the wasted work when a guess is rejected; the right " +
             "number depends on how often the draft is right for this model and workload.",
        Example: "2, from Phase 0's golden NVFP4+MTP command line.");

    public static readonly FieldHelpText CudaGraphMode = new(
        What: "vLLM's CUDA graph capture mode: how much of the forward pass gets captured as a replayable graph.",
        Why: "Graphs cut per-step launch overhead, but on this GB10 board FULL graphs crash when combined with MTP " +
             "(CUBLAS_STATUS_INTERNAL_ERROR / illegal instruction on sm121); PIECEWISE is what actually worked in Phase 0.",
        Example: "PIECEWISE — required whenever speculative decoding (MTP) is enabled on this box. Leave blank (vLLM default) for a plain model.");

    // --- Advanced ---

    public static readonly FieldHelpText Image = new(
        What: "The Docker image to run this model's container from. Blank uses the deployment's default vLLM image.",
        Why: "Different images can carry different vLLM builds/model support (the nvcr vllm:26.01 image, for example, " +
             "lacks Qwen3_5 support, which is why Phase 0 used a locally built image instead).",
        Example: "local/qwen38-vllm-dflash2:66e5414c6d75 — the default, and the only image Phase 0 tested Qwen3.5 on.");

    public static readonly FieldHelpText ExtraArgs = new(
        What: "Additional raw vLLM command-line arguments, one per line, appended after every other flag. Flags with a " +
              "field of their own (speculative/compilation config, max num seqs, max batched tokens, KV cache dtype, prefix " +
              "caching, async scheduling, mamba cache mode) are moved into that field on save, in any spelling.",
        Why: "An escape hatch for flags this editor doesn't have a dedicated field for, without needing a code change. " +
             "Owned flags are lifted because a raw copy would silently override the field (vLLM keeps the last one).",
        Example: "--disable-log-requests would suppress vLLM's own per-request access log line, on top of the flags this editor already sets.");

    public static readonly FieldHelpText AutoStart = new(
        What: "Start this model automatically when the controller process boots.",
        Why: "Without it, a model stays Stopped after a controller restart until someone starts it (or a request " +
             "arrives, if the model starts on request) — useful to avoid keeping every model resident by default.",
        Example: "Enabled for the model your tools use all day, so it's ready right after the controller comes up.");

    // --- On demand ---

    public static readonly FieldHelpText StartOnRequest = new(
        What: "Start this model when a request names it while it is stopped. The request waits until the model is Ready, then gets its answer.",
        Why: "A node that holds one model at a time can then serve several: the client names the model and the router starts it. " +
             "The wait is the model's start time, up to the startup timeout (10 minutes unless configured). llama.cpp and MLX suit this. " +
             "vLLM, SGLang and NIM take 2–5 minutes to start, so a client that waits for one of them may time out before it answers. " +
             "The AutoStartOnRequest setting turns this on for every model; this switch turns it on for one.",
        Example: "A coding client asks for a stopped llama.cpp model on a 16 GB card: the router starts it and answers once it is Ready. " +
                 "With the switch off the client gets 503 model_not_ready.");

    public static readonly FieldHelpText IdleStopMinutes = new(
        What: "Stop this model after this many minutes without a request. Blank means it is never stopped for being idle.",
        Why: "A stopped model gives its memory back to the node. The count starts at the model's last request, or at the moment it " +
             "became Ready if it has had none. A model is not stopped while a request is in flight, or while a benchmark or tuning job " +
             "runs on its node. An idle stop counts as a stop: the model does not come back after a node or controller restart. It " +
             "starts again when a request names it, if it starts on request, and at controller boot if it is set to start then.",
        Example: "15, on a model that starts on request: it leaves memory after a quiet quarter of an hour and the next request starts it again, waiting for the load.");

    public static readonly FieldHelpText StopToMakeRoom = new(
        What: "Allow MirbadLLM to stop this model when another model on the same node needs its memory to start on request.",
        Why: "When the memory check refuses a start on request, the router stops idle models that carry this switch, least recently used " +
             "first and only as many as the missing memory needs, then tries the start once more. A model with a request in flight is " +
             "never stopped, and on a node with discrete GPUs only models on the GPU that is short are considered. If the start still " +
             "does not fit, the request gets the usual 503 and nothing else is stopped.",
        Example: "Two llama.cpp models are placed on one 16 GB card that holds one of them. Both start on request and have this switch on. " +
                 "A request for the stopped one stops the idle one and starts the other.");

    // --- Tags ---

    public static readonly FieldHelpText Tags = new(
        What: "Labels for finding the model: built-in ones (Chat, Embedding, Vision, Tool calling, …) and your own, " +
              "lowercase letters, digits and hyphens, up to 24 characters, at most 12 per model.",
        Why: "The Dashboard and the Models page filter by tag (a model must have every selected tag), and the admin API " +
             "accepts ?tag=…. Tags are not launch arguments: changing them never restarts the model. \"Suggested\" marks " +
             "the tags the parsers, extra args and the checkpoint's config.json point to; they are only applied when you pick them.",
        Example: "An embedding model started with --runner pooling gets Embedding suggested; Ornith-9B with the qwen3 parsers " +
                 "gets Chat, Tool calling and Reasoning.");

    // --- Source ---

    public static readonly FieldHelpText SourceRepo = new(
        What: "The Hugging Face repo this model's checkpoint folder (Model path) was downloaded from.",
        Why: "A restore from a backup downloads every missing checkpoint again from here. Without it, a restored model " +
             "has no weights until you copy the folder back by hand. Set automatically when the model is added through " +
             "\"Download from Hugging Face\".",
        Example: "ornith-ai/Ornith-1.5-9B for a model whose Model path is Ornith-1.5-9B.");

    public static readonly FieldHelpText SourceRevision = new(
        What: "Branch, tag or commit SHA the restore downloads. Blank means the repo's default branch.",
        Why: "Pinning a commit makes a restore fetch exactly the files the model ran with, even if the repo changes later.",
        Example: "Blank for most repos; a 40-character commit SHA to pin a known-good snapshot.");

    public static readonly FieldHelpText SourceAllowPatterns = new(
        What: "Glob patterns of the files the restore fetches (comma-separated). Blank uses the download defaults.",
        Why: "Keeps a restore from pulling files the model never used, such as other GGUF quantizations in the same repo.",
        Example: "Blank for a safetensors repo; \"Model-Q4_K_M.gguf, *.json\" for one GGUF variant.");

    public static readonly FieldHelpText ExtraSources = new(
        What: "Other checkpoint folders this model needs, each with the repo it comes from.",
        Why: "Some settings point at a second folder under /models — a speculative-decoding draft model in Extra args, " +
             "for example. A restore downloads these too, before it starts the model.",
        Example: "Folder Qwen3-0.6B from Qwen/Qwen3-0.6B, referenced as --speculative-config {\"model\": \"/models/Qwen3-0.6B\", …}.");

    // --- Placement (P2, MN-118) ---

    public static readonly FieldHelpText Node = new(
        What: "The machine this model runs on: any node that has joined. Its checkpoint folder, image and " +
              "memory are that node's.",
        Why: "Each node has its own models root and memory, so the folder list, the fit check and the GPUs below all follow " +
             "the node. Once the model is saved, changing the node is a move (download there, stop here, start there), " +
             "never a side effect of saving other settings.",
        Example: "dgx for a large model that needs the GB10's 128 GiB; pc-office for a small embedding model on a 16 GiB card.");

    public static readonly FieldHelpText Gpus = new(
        What: "The GPUs of a discrete node the model runs on. One GPU, or several of the same model and memory for " +
              "tensor parallel. Saved by the GPU's UUID; the index is shown for orientation only.",
        Why: "Tensor parallel needs equal cards. The memory " +
             "budget is the total over the ticked GPUs and is split evenly. The UUID keeps the model on its card if a driver " +
             "update renumbers the GPUs. Changing GPUs is a normal edit that needs a restart; a raw --tensor-parallel-size " +
             "in Extra args is replaced by the number of ticked GPUs.",
        Example: "GPU 0 alone for a 9B model in 14 GiB; GPU 0 and GPU 1 (two RTX 5060 Ti 16 GB) for a model that needs 28 GiB.");

    public static readonly FieldHelpText Runtime = new(
        What: "The engine that serves the model, from the runtimes this node reports.",
        Why: "The settings on this page belong to the runtime. A node lists the runtimes it can run in its capabilities.",
        Example: "vLLM for most models on a Linux node.");

    public static readonly FieldHelpText KeepServing = new(
        What: "Keep answering requests for this model while a benchmark or tuning job runs on another model on the same node.",
        Why: "By default a job pauses every model on its node, so its measurements are not disturbed by other traffic. Keeping " +
             "this one serving trades some measurement noise for availability; the runs then record that noise was possible. " +
             "The model under test itself always pauses. It takes effect once node-scoped maintenance is switched on.",
        Example: "On for an embedding model that other services call all day; off for a chat model you benchmark often.");

    public static readonly FieldHelpText ImageDigest = new(
        What: "The approved digest of the image override: sha256: followed by 64 hex characters.",
        Why: "A Linux PC node only runs images pinned to an approved digest, so a model with its own image needs its digest " +
             "there. The DGX runs its locally built images by tag and needs none. Changing it needs a restart.",
        Example: "sha256:4f0c… as printed by docker buildx imagetools inspect for the image, and listed in the node's approved images.");

    public static readonly FieldHelpText MoveTarget = new(
        What: "The node the model moves to. Only online nodes that are not draining can take it.",
        Why: "A move downloads the checkpoint again on the target from the model's Hugging Face sources while the model keeps " +
             "serving here, then stops it here, switches the placement and starts it there.",
        Example: "pc-office, to free the DGX's memory for a larger model.");

    public static readonly FieldHelpText MoveToken = new(
        What: "A saved Hugging Face access token for a gated or private repo, picked by name (Access > Tokens), used for this " +
              "move's download on the target node.",
        Why: "The master reads its value as the move starts, keeps it in memory until the download ends and passes it to the " +
             "target node's download; it is never logged or sent to the browser. A move resumed after a master restart has " +
             "no token: if the download then fails, start the move again.",
        Example: "Needed for a gated repo such as meta-llama/Llama-3.1-8B-Instruct; blank for public repos.");
}
