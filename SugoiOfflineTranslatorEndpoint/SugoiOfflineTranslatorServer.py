
import os
import sys
import io
import threading
import ctypes
import subprocess

# Set UTF-8 encoding for stdout and stderr
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8')

fairseq_install_path = os.path.join(os.getcwd(), 'fairseq')
if os.path.exists(fairseq_install_path) and os.path.isdir(fairseq_install_path):
    sys.path.insert(0, fairseq_install_path)
    sys.stdout.reconfigure(encoding='utf-8')

    # Only import if the fairseq path is valid
    from fairseq.models.transformer import TransformerModel
else:
    TransformerModel = None


from flask import Flask
from flask import request
from flask_cors import CORS, cross_origin

import time
import json
import re
import argparse

from functools import lru_cache
from logging import getLogger
from logging.config import dictConfig

dictConfig({
    'version': 1,
    'formatters': {'default': {
        'format': 'SugoiSrv[%(levelname)s] %(message)s',
    }},
    'handlers': {'wsgi': {
        'class': 'logging.StreamHandler',
        'stream': 'ext://sys.stdout',
        'formatter': 'default'
    }},
    'root': {
        'level': 'INFO',
        'handlers': ['wsgi']
    }
})


LOG = getLogger("root")

def minimize_window():
    SW_MINIMIZE = 6
    kernel32 = ctypes.windll.kernel32
    user32 = ctypes.windll.user32
    window_handle = kernel32.GetConsoleWindow()

    if window_handle:
        user32.ShowWindow(window_handle, SW_MINIMIZE)
    else:
        print("Window handle not found. This script may not be running in a console window.")

class TranslateBackendBase:
    def translate(self, s):
        raise NotImplementedError()


class FairseqTranslateBackend(TranslateBackendBase):
    def __init__(self, settings):
        LOG.info("Setting up fairseq translation backend..")

        # Add logging to verify the values of the paths
        LOG.info(f"Fairseq data dir: {settings.fairseq_data_dir}")
        LOG.info(f"Fairseq model file: {settings.fairseq_model}")

        # Check for None values in the paths and raise an error if found
        if settings.fairseq_data_dir is None or settings.fairseq_model is None:
            raise ValueError("fairseq_data_dir and fairseq_model must be valid paths, not None.")
        # Check if model data dir exists
        if not os.path.exists(settings.fairseq_data_dir):
            raise FileNotFoundError(f"Data directory does not exist: {settings.fairseq_data_dir}")
        # Check if model file exists
        model_file_path = os.path.join(settings.fairseq_data_dir, settings.fairseq_model)
        if not os.path.exists(model_file_path):
            raise FileNotFoundError(f"Model file does not exist: {model_file_path}")

        self.transformer = TransformerModel.from_pretrained(
            settings.fairseq_data_dir,
            checkpoint_file=settings.fairseq_model,
            source_lang = "ja",
            target_lang = "en",
            bpe='sentencepiece',
            sentencepiece_model='./fairseq/spmModels/spm.ja.nopretok.model',
            no_repeat_ngram_size=3,
            # is_gpu=True
        )
        
        if settings.cuda:
            LOG.info("CUDA Enabled!")
            self.transformer.cuda()

    def translate(self, s):
        return self.transformer.translate(s)


class Ctranslate2TranslateBackend(TranslateBackendBase):
    def __init__(self, settings):
        LOG.info("Setting up ctranslate2 translation backend..")
        import sentencepiece as spm
        import ctranslate2

        LOG.info(f"CTranslate2 data dir:{settings.ctranslate2_data_dir}")

        '''# Helper function to find a valid model path
        def get_valid_model_path(*paths):
            for path in paths:
                if os.path.exists(path):
                    return path
            LOG.warning(f"None of the specified paths exist: {paths}. CT2 model not installed.")
            return None

        # Use the function to determine the valid paths for source and target models
        source_model_path = get_valid_model_path(
            "./ct2/spmModels/spm.ja.nopretok.model",
            "./models/spmModels/spm.ja.nopretok.model",
            "./spmModels/spm.ja.nopretok.model"
        )

        target_model_path = get_valid_model_path(
            "./ct2/spmModels/spm.en.nopretok.model",
            "./models/spmModels/spm.en.nopretok.model",
            "./spmModels/spm.en.nopretok.model"
        )

        # Initialize SentencePieceProcessor only if valid paths are found
        if source_model_path and target_model_path:
            self.source_spm = spm.SentencePieceProcessor(source_model_path)
            self.target_spm = spm.SentencePieceProcessor(target_model_path)
            LOG.info(f"Source spm model file: {source_model_path}")
            LOG.info(f"Target spm model file: {target_model_path}")
        else:
            self.source_spm = None
            self.target_spm = None
            LOG.warning("Translation models were not initialized because valid paths were not found.")'''

        spm_model_dir = os.path.join(settings.ctranslate2_data_dir, '..', 'spmModels')

        self.source_spm = spm.SentencePieceProcessor(os.path.join(spm_model_dir, "spm.ja.nopretok.model"))
        self.target_spm = spm.SentencePieceProcessor(os.path.join(spm_model_dir, "spm.en.nopretok.model"))

        # Set up the translator
        device = "cuda" if settings.cuda else "cpu"
        self.translator = ctranslate2.Translator(
            model_path=settings.ctranslate2_data_dir,
            device=device
        )

        # Log a message if CUDA is enabled
        if device == "cuda":
            LOG.info("CT2 translation device: GPU (CUDA enabled)")
        else:
            LOG.info("CT2 translation device: CPU")

    def translate(self, s):
        # Ensure that the SentencePieceProcessor is initialized before attempting to translate
        if not self.source_spm or not self.target_spm:
            LOG.error("Translation cannot proceed as the SentencePiece models were not loaded.")
            return s

        line = self.source_spm.encode(s, out_type=str)
        LOG.info(f'translating: {line}')
        results = self.translator.translate_batch(
            [line],
            beam_size=5,
            num_hypotheses=1,
            no_repeat_ngram_size=3
        )
        return self.target_spm.decode(results[0].hypotheses)[0]

ja2en = None

app = Flask(__name__)

cors = CORS(app)
app.config['CORS_HEADERS'] = 'Content-Type'

@app.route("/", methods = ['POST'])
@cross_origin()
def sendImage():
    tic = time.perf_counter()
    data = request.get_json()
    message = data.get("message")
    content = data.get("content").strip('﻿').strip();

    if (message == "close server"):
        shutdown_server()
        return

    if (message == "translate sentences"):
        t = translate(content)

        toc = time.perf_counter()
        # LOG.info(f"Request: {content}")
        LOG.info(f"Translation {round(toc-tic,2)}s): {t}")

        return json.dumps(t)

    if (message == "translate batch"):
        batch = data.get("batch")
        if isinstance(batch, list):
            batch = [s.strip('﻿').strip() for s in batch]

            translated = [
                translate(s)
                for s in batch
            ]

        toc = time.perf_counter()
        # LOG.info(f"Request: {batch}")
        LOG.info(f"Translation complete {round(toc-tic,2)}s)")

        return json.dumps(translated)


def shutdown_server():
    func = request.environ.get('werkzeug.server.shutdown')
    if func is None:
        raise RuntimeError('Not running with the Werkzeug Server')
    func()


JP_TEXT_PATTERN = re.compile("[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uff66-\uff9f]")
PLACEHOLDER_PATTERN = re.compile(r"ZM(?P<word>[A-Z])Z")


@lru_cache
def translate(content):
    if not JP_TEXT_PATTERN.search(content):
        LOG.warn(f"Content [{content}] does not seem to have jp characters, skipping translation")
        return content

    filter_line, isBracket, isPeriod, replacements = pre_translate_filter(content)

    result = ja2en.translate(filter_line)
    result = restore_placeholders(result, replacements)
    result = post_translate_filter(result)
    
    if result.endswith(".") and not result.endswith("...") and not isPeriod and not isBracket:
        result = result[:-1]
    
    result = add_double_quote(result, isBracket)
    
    LOG.info(f"{content} => {result}")
    return result


def filter_placeholders(text):
    replacements = []
    old = text

    for match in PLACEHOLDER_PATTERN.finditer(text):
        word = match.group("word")
        whole = match.group()
        replacement = f"@#{word}"
        if replacement not in text:
            replacements.append((whole, replacement))

    for whole, replacement in replacements:
        text = text.replace(whole, replacement)

    if replacements:    
        LOG.info(f"Replaced placholders:[{old}] => [{text}]")

    return text, replacements


def restore_placeholders(text, replacements):
    for whole, replacement in replacements:
        text = text.replace(replacement, whole)
    return text


def pre_translate_filter(data):
    # data = data.replace('\n', '')
    # data = data.replace('\u3000', '')  # remove "　"
    # data = data.replace('\u200b', '')
    data = data.strip()

    isBracket = data.endswith("」") and data.startswith('「')
    isPeriod = data.endswith("。")
    data, replacements = filter_placeholders(data)

    return data, isBracket, isPeriod, replacements


def post_translate_filter(data):
    text = data
    text = text.replace('<unk>', ' ')
    # text = text.replace('―', '-')
    
    start = text[0]
    end = text[-1]

    start_quotes = ('「', '”', '“', '"', "'")
    end_quotes = ('」',  '“', '”', '"', "'")

    if start in start_quotes:
        text = text[1:]

    if end in end_quotes:
        text = text[:-1]

    text = text.strip()
    text = text[0].upper() + text[1:]

    return text


def add_double_quote(data, isBracket):
    en_text = data
    if isBracket:
        en_text = f'"{data}"'

    return en_text

def parse_commandline_args():
    # Helper function to get the valid directory path
    def get_valid_data_dir(*paths):
        for path in paths:
            if os.path.exists(path) and os.path.isdir(path):
                return path
        LOG.warning(f"None of the specified paths exist: {paths}. CT2 model not installed.")
        return None

    # Check for valid ctranslate2 data directory
    # ctranslate2_data_dir = get_valid_data_dir(
    #    "./ct2/ct2_models/",
    #    "./models/ct2Model/",
    #    "./ct2Model/"
    #)

    # Set up the argument parser
    parser = argparse.ArgumentParser(description="SugoiOfflineTranslator backend server")
    parser.add_argument('port', type=int, help="The port to listen to")
    parser.add_argument('--fairseq-data-dir', type=str, default="./fairseq/japaneseModel/",
                        help="Directory containing the fairseq pretrained models and related files")
    parser.add_argument('--fairseq-model', type=str, default="big.pretrain.pt",
                        help="Name of the pretrained model to use")
    parser.add_argument('--cuda', action="store_true",
                        help="Run translations on the GPU via CUDA")
    parser.add_argument('--ctranslate2', action="store_true",
                        help="Enables the use of ctranslate2 instead of fairseq")
    #parser.add_argument('--ctranslate2-data-dir', type=str, default=ctranslate2_data_dir,
    #                    help="Directory to use for ctranslate2 model")
    parser.add_argument('--ctranslate2-data-dir', type=str, default=None,
                        help="Directory to use for ctranslate2 model")

    return parser.parse_args()

def main():
    global ja2en

    try:
        args = parse_commandline_args()

        minimize_window()

        from flask import cli
        cli.show_server_banner = lambda *_: None
        
        #if not args.ctranslate2:
            # LOG.info(f"args.ctranslate2 is {args.ctranslate2} (type: {type(args.ctranslate2)})")
        #    if not os.path.exists(args.fairseq_data_dir):
        #        LOG.warning(f"Fairseq module not found. Using CT2. Checked path: {args.fairseq_data_dir}")
        #        ja2en = Ctranslate2TranslateBackend(args)
        #    else:
        #        try:
        #            ja2en = FairseqTranslateBackend(args)
        #        except Exception as e:
        #            LOG.error(f"Error initializing FairseqTranslateBackend: {e}")
        #            ja2en = Ctranslate2TranslateBackend(args)
        #else:
        #    ja2en = Ctranslate2TranslateBackend(args)

        if args.ctranslate2:
            if args.ctranslate2_data_dir is None:
                LOG.error(f"Unable to find valid model data under {os.getcwd()}")
                sys.exit(2)
            else:
                ja2en = Ctranslate2TranslateBackend(args)
        else:
            ja2en = FairseqTranslateBackend(args)


        app.run(host='127.0.0.1', port=args.port)
    
    except ValueError as e:
        LOG.error(f"Initialization error: {e}")
    except Exception as e:
        LOG.exception(f"An unexpected error occurred: {e}")


if __name__ == "__main__":
    main()

