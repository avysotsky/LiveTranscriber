"""Download and convert Helsinki-NLP/opus-mt-en-ru to offline CTranslate2 INT8.

Safe to rerun after an interrupted setup. No writes to unrelated directories.
"""
import argparse
from pathlib import Path

REQUIRED = ("model.bin", "source.spm", "target.spm", "tokenizer_config.json")
MODEL = "Helsinki-NLP/opus-mt-en-ru"


def is_complete(directory: Path) -> bool:
    return all((directory / name).is_file() for name in REQUIRED)


def prepare(output: Path, tokenizer_cls=None, converter_cls=None) -> None:
    output = Path(output).expanduser().resolve()
    if is_complete(output):
        print("Model already prepared:", output)
        return

    # Imports are deferred so the completeness check needs no ML libraries.
    if tokenizer_cls is None:
        from transformers import MarianTokenizer
        tokenizer_cls = MarianTokenizer
    if converter_cls is None:
        from ctranslate2.converters import TransformersConverter
        converter_cls = TransformersConverter

    tokenizer = tokenizer_cls.from_pretrained(MODEL)
    output.mkdir(parents=True, exist_ok=True)
    # CTranslate2 defaults to force=False and raises when the destination
    # exists, including the directory just created above. Allow conversion
    # to resume/overwrite incomplete files in this dedicated model folder.
    converter_cls(MODEL).convert(str(output), quantization="int8", force=True)
    tokenizer.save_pretrained(str(output))

    missing = [name for name in REQUIRED if not (output / name).is_file()]
    if missing:
        raise RuntimeError("Model preparation incomplete: " + ", ".join(missing))
    print("Converted INT8 model and tokenizer saved to", output)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, help="Offline converted model directory")
    args = parser.parse_args()
    prepare(Path(args.output))


if __name__ == "__main__":
    main()
