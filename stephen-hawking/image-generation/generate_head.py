"""WSL: run with john-pork/image-generation/.venv/bin/python; uses the idle RTX 4090."""
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ART = ROOT.parent / 'art'
SHARED = ROOT.parents[1] / 'john-pork/image-generation'
GPU = 'GPU-e665a1dd-c279-05ca-a396-e7b95d783790'
os.environ['CUDA_VISIBLE_DEVICES'] = GPU
os.environ['HF_HOME'] = str(SHARED / 'hf-cache')
os.environ['HF_HUB_DISABLE_IMPLICIT_TOKEN'] = '1'
for variable, directory in [('XDG_CACHE_HOME', 'cache'), ('TORCH_HOME', 'torch-cache'),
                            ('TRITON_CACHE_DIR', 'triton-cache')]:
    os.environ[variable] = str(ROOT / directory)

import json
import time
import torch
from PIL import Image
from diffusers import Flux2KleinPipeline
from huggingface_hub import snapshot_download

MODEL = 'black-forest-labs/FLUX.2-klein-4B'
REVISION = 'e7b7dc27f91deacad38e78976d1f2b499d76a294'
assert torch.cuda.device_count() == 1
assert '4090' in torch.cuda.get_device_name(0)
model_path = snapshot_download(
    MODEL, revision=REVISION, local_files_only=True,
    allow_patterns=['model_index.json', 'scheduler/*', 'text_encoder/*',
                    'tokenizer/*', 'transformer/*', 'vae/*'])
print('GPU_CONFIRMED', GPU, torch.cuda.get_device_name(0), flush=True)
pipe = Flux2KleinPipeline.from_pretrained(model_path, torch_dtype=torch.bfloat16)
pipe.enable_model_cpu_offload(gpu_id=0)
pipe.vae.enable_tiling()
reference = Image.open(ART / 'references/face-reference.png').convert('RGB')
prompt = (
    'Edit the supplied Stephen Hawking portrait into a clean, isolated three-dimensional game-character head. '
    'Keep EXACTLY the same distinctive face, head angle, asymmetric open-mouthed expression, visible teeth, '
    'large thin metal-framed eyeglasses, and sparse side-combed gray-brown hair as in the photograph. '
    'Do not straighten or beautify his face. Do not replace him with a different elderly man. '
    'Finish the complete solid skull, ears, chin, and a short bare neck as a coherent sculpted head. '
    'Remove the wheelchair, headrest, clothing and shoulders entirely. One head only. '
    'Style: a recognizable hand-painted 2001 GameCube character, natural warm skin, simplified clean surfaces, '
    'clearly modeled nose, lips, cheeks, ears and glasses; no photographic background baked into the head. '
    'Soft even studio lighting, plain pale-gray background, no text, no watermark. '
    'The whole head including hair and ears must be visible, centered with generous empty margin.'
)
metadata = {'model': MODEL, 'revision': REVISION, 'gpu_uuid': GPU,
            'reference': 'references/face-reference.png', 'outputs': []}
for seed in (27183,):
    started = time.time()
    with torch.inference_mode():
        image = pipe(image=[reference], prompt=prompt, height=768, width=768,
                     guidance_scale=1.0, num_inference_steps=4,
                     generator=torch.Generator(device='cuda').manual_seed(seed)).images[0]
    name = f'head-candidate-{seed}.png'
    image.save(ART / name)
    metadata['outputs'].append({'file': name, 'prompt': prompt, 'seed': seed,
                                'width': 768, 'height': 768, 'steps': 4,
                                'guidance_scale': 1.0, 'elapsed_seconds': time.time() - started})
    (ART / 'head-generation.json').write_text(json.dumps(metadata, indent=2))
    print('HEAD_IMAGE_READY', name, flush=True)
del pipe
torch.cuda.empty_cache()
print('HEAD_IMAGES_COMPLETE_GPU_RELEASED', flush=True)
