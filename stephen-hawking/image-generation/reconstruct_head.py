"""Submit the proven native Pixal3D workflow against the selected head image."""
import json
import shutil
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ART = ROOT.parent / 'art'
SHARED = ROOT.parents[1] / 'john-pork/image-generation'
API = 'http://127.0.0.1:8197'


def request(path, data=None):
    req = urllib.request.Request(API + path,
                                 data=None if data is None else json.dumps(data).encode(),
                                 headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=120) as response:
        return json.load(response)


workflow = json.loads((SHARED / 'pixal-api-workflow.json').read_text())
workflow['122']['inputs']['image'] = 'head-source.png'
# Use the selected portrait's explicit alpha matte throughout conditioning.
workflow['1000'] = {'class_type': 'InvertMask', 'inputs': {'mask': ['122', 1]}}
workflow['303']['inputs']['mask'] = ['1000', 0]
workflow['312']['inputs']['masks'] = ['1000', 0]
del workflow['192'], workflow['193']
workflow['186']['inputs']['target_face_count'] = 12000
workflow['999']['inputs']['filename_prefix'] = 'hawking-head'
# A head fills considerably more of the voxel volume than the earlier full-body
# John Pork input. Use Pixal3D's native pure-512 path rather than the 1024 cascade.
workflow['92']['inputs']['samples'] = ['18', 0]
workflow['98']['inputs']['positive'] = ['91', 0]
workflow['98']['inputs']['negative'] = ['91', 1]
workflow['98']['inputs']['shape_latent'] = ['18', 0]
del workflow['94'], workflow['23']
workflow['147']['inputs']['texture_size'] = 512
workflow['288']['inputs']['value'] = 512
workflow['224']['inputs']['resolution'] = 512
workflow['233']['inputs']['resolution'] = 256
(ROOT / 'head-api-workflow.json').write_text(json.dumps(workflow, indent=2))
info = request('/object_info')
missing = {node['class_type'] for node in workflow.values()} - info.keys()
if missing:
    raise RuntimeError(f'Missing native reconstruction nodes: {sorted(missing)}')
response = request('/prompt', {'prompt': workflow, 'client_id': 'hawking-head-local'})
(ROOT / 'head-submission.json').write_text(json.dumps(response, indent=2))
print('HEAD_RECONSTRUCTION_SUBMITTED', response, flush=True)
identifier = response['prompt_id']
while True:
    history = request('/history/' + identifier)
    if identifier in history:
        result = history[identifier]
        (ROOT / 'head-result.json').write_text(json.dumps(result, indent=2))
        if result.get('status', {}).get('status_str') != 'success':
            raise RuntimeError('Head reconstruction failed; inspect head-result.json')
        break
    time.sleep(10)
files = [item for output in result['outputs'].values() for key in ['3d', 'images', 'files']
         for item in output.get(key, []) if str(item.get('filename', '')).endswith('.glb')]
if len(files) != 1:
    raise RuntimeError(f'Expected one GLB in this job output, received {files!r}')
source = ART / 'pixal-output' / files[0].get('subfolder', '') / files[0]['filename']
shutil.copyfile(source, ART / 'hawking-head-generated.glb')
print('HEAD_GLB_READY', ART / 'hawking-head-generated.glb', flush=True)
