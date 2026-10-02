# 这不是通过路径引用的，而是 uv 已经把 sample-project 包安装到环境中
# 因此不需要配置任何 PYTHON_PATH 等环境变量，也不会碰到找不到模块的问题
#（注意 exe 本身不在包内。所以包内是引用不到 main.py 的）
import sample_project
output = sample_project.hello()
print(output)