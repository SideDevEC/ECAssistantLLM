# Third-Party Notices

ECAssistantLLM is MIT licensed (see LICENSE). It builds on the following third-party
components. ECAssistantLLM does **not** redistribute model weights or backend runtime
binaries in its packages — the setup wizard downloads them directly from their
respective sources at install time, and each artifact ships with its own license file.

## Prism ML — llama.cpp fork (backend runtime)

- **Used for:** ternary-packed Bonsai model inference (PTQ1_0 / PQ2_0 kernels)
- **Source:** https://github.com/PrismML-Eng/llama.cpp (pinned releases, e.g. `prism-b10685-7dffb15`)
- **License:** MIT (inherited from upstream https://github.com/ggerganov/llama.cpp).
  The release archive distributed by Prism ML contains its own LICENSE file, which
  accompanies the installed binaries under `<serverRoot>/backends/`.
- **Distribution note:** the ECAssistant setup wizard downloads the archive directly
  from Prism ML's GitHub releases (checksum-verified) and installs it unmodified.

## Prism ML — Bonsai models (model weights)

- **Used for:** the `qwen38-bonsai2` process-backend model (e.g. Ternary-Bonsai-2-27B)
- **Source:** https://github.com/PrismML-Eng / https://huggingface.co/prism-ml
- **License:** Apache License 2.0 (weights and base model). The full Apache 2.0 license
  text ships with the weights repository.
- **Distribution note:** weights are downloaded directly from Prism ML by the user
  (setup wizard) and are NOT included in any ECAssistant package or repository.
- Qwen3.8-27B base model attribution: Prism ML documents the base model lineage;
  refer to the Bonsai model repository for its Datasheet/NOTICE files.

## LLamaSharp (stock inference backend)

- **Used for:** in-process model inference
- **Source:** https://github.com/SciSharp/LLamaSharp — MIT
- **Bundled as:** NuGet dependency (LLamaSharp 0.27.0 + CPU/CUDA12/Vulkan backends),
  which itself wraps llama.cpp (MIT). NuGet distributes license metadata per package.

## Microsoft.Extensions.Logging.Abstractions

- **Bundled as:** NuGet dependency — MIT — https://github.com/dotnet/runtime

---

If you redistribute a combined installation of ECAssistantLLM (server + backend runtimes
+ model weights), you are responsible for preserving the license files described above.
