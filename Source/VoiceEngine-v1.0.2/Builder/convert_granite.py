from pathlib import Path
import sys
import numpy as np
import torch
src=Path(sys.argv[1]); dst=Path(sys.argv[2]); dst.parent.mkdir(parents=True,exist_ok=True)
obj=torch.load(src,map_location='cpu',weights_only=True)
if isinstance(obj,dict):
    for key in ('voice','embedding','style','tensor'):
        if key in obj and torch.is_tensor(obj[key]): obj=obj[key]; break
if not torch.is_tensor(obj):
    raise SystemExit(f'Unsupported Granite .pt payload: {type(obj)!r}')
a=obj.detach().cpu().float().numpy()
# KokoroSharp custom voices are NumPy tensors with exact [510, 1, 256] shape.
# Community .pt exports may occasionally omit the singleton middle dimension.
if a.ndim == 2 and a.shape == (510, 256):
    a = a.reshape(510, 1, 256)
if a.ndim != 3 or a.shape[0] != 510 or a.shape[1] != 1 or a.shape[2] != 256:
    raise SystemExit(f'Unexpected Granite embedding shape: {a.shape}; expected (510, 1, 256)')
np.save(dst,a,allow_pickle=False)
print(f'Converted {src.name} -> {dst.name} shape={a.shape}')
