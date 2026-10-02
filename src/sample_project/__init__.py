def hello() -> str:
    import os
    import shutil
    
    return (f"cpu: {os.sched_getaffinity(0)}\n"
            f"nvcc: {shutil.which("nvcc")}\n"
            f"nvidia-smi: {shutil.which("nvidia-smi")}")