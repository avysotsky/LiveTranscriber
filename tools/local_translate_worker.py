"""Offline UTF-8 JSON-lines worker. stdout is strictly machine-readable.

No network requests, no transcripts or credentials on stderr.
The model/tokenizer must have been prepared before app startup.
"""
import json
import os
import sys
from pathlib import Path


def emit(message):
    sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main():
    if len(sys.argv) != 2:
        emit({"type": "error", "error": "Expected a local model directory."})
        return 2

    model_dir = Path(sys.argv[1])
    if not (model_dir / "model.bin").is_file():
        emit({"type": "error", "error": "Converted OPUS-MT model.bin is missing."})
        return 2

    # Defense in depth: do not fetch tokenizers or weights from the internet.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    try:
        import ctranslate2
        from transformers import MarianTokenizer

        tokenizer = MarianTokenizer.from_pretrained(str(model_dir), local_files_only=True)
        translator = ctranslate2.Translator(
            str(model_dir),
            device="cpu",
            compute_type="int8",
            inter_threads=1,
            intra_threads=2,
        )
    except Exception:
        emit({"type": "error", "error": "Could not load local translation model. See setup instructions."})
        return 3

    emit({"type": "ready"})
    for line in sys.stdin:
        try:
            request = json.loads(line)
            request_id = request.get("id")
            text = request.get("text", "")
            if not isinstance(request_id, int) or not isinstance(text, str):
                emit({"type": "error", "id": request_id, "error": "Invalid request."})
                continue
            if not text.strip():
                emit({"id": request_id, "translation": ""})
                continue

            translated = []
            # Translation pipeline batches finalized phrases with newlines.
            # Preserve their original order; prevent very long tokenizer inputs.
            for paragraph in text.split("\n"):
                if not paragraph.strip():
                    continue
                source_ids = tokenizer.encode(paragraph.strip(), truncation=True, max_length=480)
                source_tokens = tokenizer.convert_ids_to_tokens(source_ids)
                output = translator.translate_batch([source_tokens], beam_size=2, max_decoding_length=256)
                target_tokens = output[0].hypotheses[0]
                target_text = tokenizer.decode(
                    tokenizer.convert_tokens_to_ids(target_tokens), skip_special_tokens=True)
                translated.append(target_text.strip())

            emit({"id": request_id, "translation": "\n".join(translated)})
        except Exception:
            # Never print interview text, model internals, or full stack traces.
            emit({"type": "error", "id": request.get("id") if isinstance(request, dict) else None,
                  "error": "Offline translation failed for this phrase."})
    return 0


if __name__ == "__main__":
    sys.exit(main())
