"""Offline model setup regression tests. No real network, model or PyTorch required."""
import tempfile
import unittest
from pathlib import Path
from prepare_opus_mt import MODEL, is_complete, prepare


class FakeTokenizer:
    loads = 0
    saves = 0

    @classmethod
    def from_pretrained(cls, model):
        assert model == MODEL
        cls.loads += 1
        return cls()

    def save_pretrained(self, output):
        FakeTokenizer.saves += 1
        for name in ("source.spm", "target.spm", "tokenizer_config.json"):
            (Path(output) / name).write_text("stub", encoding="utf-8")


class FakeConverter:
    calls = 0

    def __init__(self, model):
        assert model == MODEL

    def convert(self, output, quantization, force):
        assert force is True, "Conversion must support an existing model directory"
        assert quantization == "int8"
        FakeConverter.calls += 1
        (Path(output) / "model.bin").write_text("stub", encoding="utf-8")


class PrepareModelTests(unittest.TestCase):
    def setUp(self):
        FakeConverter.calls = 0
        FakeTokenizer.loads = 0
        FakeTokenizer.saves = 0

    def test_nonexistent_destination_converts_then_is_idempotent(self):
        with tempfile.TemporaryDirectory() as root:
            output = Path(root) / "offline-model"
            prepare(output, FakeTokenizer, FakeConverter)
            self.assertTrue(is_complete(output))
            self.assertEqual(1, FakeConverter.calls)
            prepare(output, FakeTokenizer, FakeConverter)
            self.assertEqual(1, FakeConverter.calls)

    def test_existing_incomplete_directory_recovers_without_manual_delete(self):
        with tempfile.TemporaryDirectory() as root:
            output = Path(root) / "offline-model"
            output.mkdir()
            (output / "source.spm").write_text("partial", encoding="utf-8")
            prepare(output, FakeTokenizer, FakeConverter)
            self.assertTrue(is_complete(output))
            self.assertEqual(1, FakeConverter.calls)

    def test_missing_target_tokenizer_forces_repair(self):
        with tempfile.TemporaryDirectory() as root:
            output = Path(root) / "offline-model"
            output.mkdir()
            (output / "model.bin").write_text("partial", encoding="utf-8")
            (output / "source.spm").write_text("partial", encoding="utf-8")
            prepare(output, FakeTokenizer, FakeConverter)
            self.assertTrue(is_complete(output))
            self.assertEqual(1, FakeConverter.calls)


if __name__ == "__main__":
    unittest.main()
