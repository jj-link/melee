"""Reuse the installed ComfyUI and model cache, with isolated character outputs."""
import os
import runpy
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SHARED = ROOT.parents[1] / 'john-pork/image-generation'
COMFY = SHARED / 'ComfyUI'
for directory in ['comfy-user', 'intermediates']:
    (ROOT / directory).mkdir(parents=True, exist_ok=True)
(ROOT.parent / 'art/pixal-output').mkdir(parents=True, exist_ok=True)
os.environ['CUDA_VISIBLE_DEVICES'] = 'GPU-e665a1dd-c279-05ca-a396-e7b95d783790'
os.environ['HF_HOME'] = str(SHARED / 'hf-cache')
os.environ['HF_HUB_DISABLE_IMPLICIT_TOKEN'] = '1'
for variable, directory in [('XDG_CACHE_HOME', 'cache'), ('TORCH_HOME', 'torch-cache'),
                            ('TRITON_CACHE_DIR', 'triton-cache'), ('MPLCONFIGDIR', 'matplotlib-cache')]:
    os.environ[variable] = str(ROOT / directory)
os.chdir(COMFY)
sys.path.insert(0, str(COMFY))
sys.argv = ['main.py', '--listen', '127.0.0.1', '--port', '8197', '--disable-auto-launch',
            '--output-directory', str(ROOT.parent / 'art/pixal-output'),
            '--input-directory', str(ROOT.parent / 'art'),
            '--user-directory', str(ROOT / 'comfy-user'), '--lowvram', '--cache-none']
# ComfyUI's --temp-directory appends a literal 'temp' child. Set its native
# folder registry instead so agent-created work stays in the project directory.
import comfy.options
comfy.options.enable_args_parsing()
import folder_paths
folder_paths.set_temp_directory(str(ROOT / 'intermediates'))
runpy.run_path(str(COMFY / 'main.py'), run_name='__main__')
