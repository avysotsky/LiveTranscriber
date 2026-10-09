"""Download and convert the Helsinki-NLP EN->RU Marian model ONCE.

After this setup, the application uses only local model/tokenizer files.
"""
import argparse
import os
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, help="Offline converted model directory")
    args = parser.parse_args()
    output = Path(args.output).expanduser().resolve()
    if (output / "model.bin").is_file() and (output / "source.spm").is_file():
        print("Model already prepared:", output)
        return

    from transformers import MarianTokenizer
    from ctranslate2.converters import TransformersConverter

    repo = "Helsinki-NLP/opus-mt-en-ru"
    # Network access is intended ONLY for this one-time setup operation.
    tokenizer = MarianTokenizer.from_pretrained(repo)
    output.mkdir(parents=True, exist_ok=True)
    TransformersConverter(repo).convert(str(output), quantization="int8")
    tokenizer.save_pretrained(str(output))
    print("Converted INT8 model and tokenizer saved to", output)


if __name__ == "__main__":
    main()
