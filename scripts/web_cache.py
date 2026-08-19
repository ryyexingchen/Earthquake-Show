import requests
import os
import json
import sys

def cache_webpage(url, output_dir="web_cache"):
    """
    将指定网址的网页源码缓存到本地，并返回结构化的执行结果（AI友好）。
    """
    # 1. 确保输出目录存在
    if not os.path.exists(output_dir):
        os.makedirs(output_dir)

    # 2. 设置请求头，模拟真实浏览器，防止被基础反爬拦截
    headers = {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36'
    }

    result = {"url": url, "status": "failed", "message": "", "file_path": ""}

    try:
        # 3. 发送 HTTP GET 请求
        response = requests.get(url, headers=headers, timeout=10)
        
        # 4. 检查 HTTP 状态码
        if response.status_code != 200:
            result["message"] = f"HTTP Request failed with status code: {response.status_code}"
            return result

        # 5. 处理编码问题，防止中文乱码
        response.encoding = response.apparent_encoding
        
        # 6. 生成保存路径并写入文件
        # 简单处理文件名，去除特殊字符
        safe_filename = url.split("//")[-1].replace("/", "_").split("?")[0] + ".html"
        file_path = os.path.join(output_dir, safe_filename)
        
        with open(file_path, 'w', encoding='utf-8') as f:
            f.write(response.text)

        # 7. 更新成功状态
        result["status"] = "success"
        result["message"] = "Webpage cached successfully."
        result["file_path"] = file_path

    except requests.exceptions.RequestException as e:
        result["message"] = f"Network or Request Error: {str(e)}"
    except Exception as e:
        result["message"] = f"Unexpected Error: {str(e)}"

    return result

# 如果直接运行此脚本
if __name__ == "__main__":
    # target_url = sys.argv[1] if len(sys.argv) > 1 else "https://httpbin.org/html"
    target_url = "https://www.kyoshin.bosai.go.jp/ja/about_pubdata/"
    res = cache_webpage(target_url)
    
    # 输出 JSON 格式，方便 AI 或外部脚本解析
    print(json.dumps(res, indent=2, ensure_ascii=False))