# Semantic Image Search

Semantic search finds images by **what they show**, not by filename: query *"dog on the beach"* and get `IMG_2043.jpg`. It runs fully locally — [EmbeddingGemma 2](https://huggingface.co/google/embeddinggemma-2) (Google DeepMind's open multimodal embedding model, 768-dim vector space, Matryoshka-truncated to 256d here) served by your local [Ollama](https://ollama.com) instance. Images and queries never leave your machine.

## Setup

1. Install [Ollama](https://ollama.com) and make sure it is running (`ollama serve` starts it; the desktop app autostarts it).
2. Pull the multimodal embedding model (~1.3 GB):

   ```
   ollama pull embeddinggemma-2
   ```

   The default tag (740m) embeds text **and** images. The smaller `270m`/`570m` tags are text-only and cannot embed images.

3. In EverythingToolbar: **Settings → Semantic search → Enable semantic search**, then **Build index**. The build runs in the background and is resumable — every 200 images are checkpointed, and you can pause/resume at any time. Searches already work against the partially built index.

## Using it

- Enable it once in settings; then pick the **Semantic** filter in the filter bar (or type `sem:` in the search box). While the filter is active, results come from the vector index instead of Everything.
- The quick-settings menu (⚙ in the search window) has a **Semantic search** toggle that shows/hides the filter.
- If Ollama is down, the model is missing, or the index is empty, a notice is shown and Everything serves the query as usual.
- Incremental updates: on startup (while enabled), anything new or changed is embedded automatically. Deleted files are filtered out of results; use **Rebuild index** occasionally to reclaim their space.

## Command line (`semcli`)

The `EverythingToolbar.Cli` project builds `semcli.exe`, which shares `settings.ini` and the index with the toolbar:

```
semcli status                        # Ollama/model/index status
semcli search "sunset over water"    # prints "score<TAB>path", -k N limits results
semcli index                         # update index from Everything
semcli index --path D:\Photos        # update index from a plain folder (no Everything needed)
semcli index --rebuild               # discard and rebuild
semcli index --max 500               # cap the number of images (testing)
```

## Where things live

| Artifact | Path |
| --- | --- |
| Vector index (int8-quantized, ~256 bytes/image) | `%APPDATA%\EverythingToolbar\semantic-index.bin` |
| Settings (`SemanticOllamaUrl`, `SemanticModelTag`, …) | `%APPDATA%\EverythingToolbar\settings.ini` |
| Log | `%TEMP%\EverythingToolbar.log` |

Changing the model tag invalidates the index automatically (vectors from different models live in different spaces).

## Performance notes

- **Search**: one text embedding per keystroke (~tens of ms on GPU) plus a linear scan over quantized vectors — ~100k images scan in tens of milliseconds.
- **Indexing** is the expensive part and depends on your GPU/CPU: roughly 10–50 images/s on a modern GPU, single digits on CPU. A 10k-photo library takes minutes on GPU, potentially hours on CPU — leave it paused/resuming across sessions, it checkpoints.
- Memory: ~0.25 KB per indexed image plus the path strings (100k photos ≈ 30–50 MB RAM).
