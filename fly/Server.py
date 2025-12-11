"""
Subtitle Translation Server for Fly.io
Sử dụng Gemini API với rate limiting và retry logic
"""

import asyncio
import time
import uuid
import httpx
from fastapi import FastAPI, BackgroundTasks, HTTPException
from pydantic import BaseModel
from typing import Optional
from collections import defaultdict
from contextlib import asynccontextmanager
import logging

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# ============== MODELS ==============

class SubtitleLine(BaseModel):
    index: int
    text: str

class TranslationRequest(BaseModel):
    model: str = "gemini-2.5-flash"
    prompt: str
    lines: list[SubtitleLine]
    systemInstruction: str
    sessionId: str
    apiKeys: list[str]
    batchSize: int = 30
    thinkingBudget: Optional[int] = None  # Token budget cho thinking (0 = tắt, None = không dùng)
    callbackUrl: Optional[str] = None  # URL để gửi kết quả và thống kê API key usage

class ConfigUpdate(BaseModel):
    rpm: Optional[int] = None
    maxRetries: Optional[int] = None

class TranslatedLine(BaseModel):
    index: int
    original: str
    translated: str

class ApiKeyUsage(BaseModel):
    apiKey: str
    maskedKey: str  # Key đã che (chỉ hiện 8 ký tự đầu + 4 ký tự cuối)
    requestCount: int
    successCount: int
    failureCount: int

class JobStatus(BaseModel):
    sessionId: str
    status: str  # pending, processing, completed, failed
    progress: float
    totalLines: int
    completedLines: int
    results: list[TranslatedLine]
    error: Optional[str] = None
    apiKeyUsage: dict[str, dict] = {}  # apiKey -> {requests, success, failure}
    callbackUrl: Optional[str] = None

# ============== GLOBAL STATE ==============

class ServerConfig:
    def __init__(self):
        self.rpm = 5  # requests per minute cho toàn server
        self.max_retries = 5
        self.retry_delay_base = 2  # seconds, exponential backoff

config = ServerConfig()

# Job storage: sessionId -> JobStatus
jobs: dict[str, JobStatus] = {}

# Rate limiting: tracking request times
request_times: list[float] = []
rate_limit_lock = asyncio.Lock()

# ============== RATE LIMITER ==============

def mask_api_key(api_key: str) -> str:
    """Che API key, chỉ hiện 8 ký tự đầu + 4 ký tự cuối"""
    if len(api_key) <= 12:
        return api_key[:4] + "****" + api_key[-4:]
    return api_key[:8] + "****" + api_key[-4:]

async def send_callback(callback_url: str, payload: dict):
    """Gửi callback về server của user"""
    try:
        async with httpx.AsyncClient() as client:
            response = await client.post(
                callback_url,
                json=payload,
                timeout=30.0,
                headers={"Content-Type": "application/json"}
            )
            logger.info(f"Callback sent to {callback_url}: {response.status_code}")
            return response.status_code == 200
    except Exception as e:
        logger.error(f"Callback failed: {e}")
        return False

async def wait_for_rate_limit():
    """Chờ đến khi có thể gửi request mới theo RPM"""
    async with rate_limit_lock:
        now = time.time()
        # Xóa các request cũ hơn 60s
        while request_times and request_times[0] < now - 60:
            request_times.pop(0)
        
        # Nếu đã đạt giới hạn, chờ
        if len(request_times) >= config.rpm:
            wait_time = 60 - (now - request_times[0]) + 0.1
            if wait_time > 0:
                logger.info(f"Rate limit reached, waiting {wait_time:.1f}s")
                await asyncio.sleep(wait_time)
        
        # Ghi nhận request mới
        request_times.append(time.time())

# ============== GEMINI API CALLER ==============

# Models hỗ trợ thinking
THINKING_SUPPORTED_MODELS = [
    "gemini-2.5-flash",
    "gemini-2.5-pro", 
    "gemini-2.5-flash-preview-05-20",
    "gemini-2.5-pro-preview-05-06",
]

def model_supports_thinking(model: str) -> bool:
    """Kiểm tra model có hỗ trợ thinking không"""
    model_lower = model.lower()
    for supported in THINKING_SUPPORTED_MODELS:
        if supported in model_lower or model_lower in supported:
            return True
    # Check pattern gemini-2.5-*
    if "gemini-2.5" in model_lower or "2.5-flash" in model_lower or "2.5-pro" in model_lower:
        return True
    return False

async def call_gemini_api(
    client: httpx.AsyncClient,
    api_key: str,
    model: str,
    prompt: str,
    system_instruction: str,
    lines: list[SubtitleLine],
    thinking_budget: Optional[int] = None
) -> tuple[bool, str, bool]:
    """
    Gọi Gemini API
    Returns: (success, result_or_error, should_try_another_key)
    """
    # Xây dựng nội dung cần dịch
    lines_text = "\n".join([f"{line.index}|{line.text}" for line in lines])
    full_prompt = f"{prompt}\n\n{lines_text}"
    
    # Payload theo format Gemini API
    payload = {
        "contents": [
            {
                "parts": [{"text": full_prompt}]
            }
        ],
        "systemInstruction": {
            "parts": [{"text": system_instruction}]
        },
        "generationConfig": {
            "temperature": 0.3,
            "topP": 0.95,
            "maxOutputTokens": 8192
        }
    }
    
    # Thêm thinkingConfig nếu model hỗ trợ và có budget
    if thinking_budget is not None and model_supports_thinking(model):
        payload["generationConfig"]["thinkingConfig"] = {
            "thinkingBudget": thinking_budget
        }
        logger.info(f"Thinking enabled with budget: {thinking_budget}")
    
    url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={api_key}"
    
    try:
        response = await client.post(url, json=payload, timeout=120.0)
        
        if response.status_code == 200:
            data = response.json()
            if "candidates" in data and data["candidates"]:
                candidate = data["candidates"][0]
                content = candidate.get("content", {})
                parts = content.get("parts", [])
                
                # Extract text từ parts (bỏ qua thought parts nếu có)
                text_parts = []
                for part in parts:
                    if "text" in part:
                        text_parts.append(part["text"])
                    # Nếu có thought, log nhưng không include
                    elif "thought" in part:
                        logger.debug(f"Thinking output: {part['thought'][:100]}...")
                
                if text_parts:
                    text = "\n".join(text_parts)
                    return True, text, False
                    
            return False, "No text in response", False
        
        elif response.status_code == 429:
            # Rate limit - thử key khác
            logger.warning(f"API key rate limited (429)")
            return False, "Rate limited", True
        
        elif response.status_code == 400:
            error_data = response.json()
            error_msg = error_data.get("error", {}).get("message", "Bad request")
            # API key không hợp lệ - thử key khác nhưng không tính vào RPM
            if "API_KEY" in error_msg.upper() or "INVALID" in error_msg.upper():
                logger.warning(f"Invalid API key")
                return False, "Invalid API key", True
            return False, error_msg, False
        
        elif response.status_code == 403:
            logger.warning(f"API key forbidden (403)")
            return False, "API key forbidden", True
        
        else:
            return False, f"HTTP {response.status_code}: {response.text[:200]}", True
            
    except httpx.TimeoutException:
        return False, "Request timeout", True
    except Exception as e:
        return False, str(e), True

# ============== TRANSLATION WORKER ==============

async def process_translation_job(
    session_id: str,
    model: str,
    prompt: str,
    system_instruction: str,
    lines: list[SubtitleLine],
    api_keys: list[str],
    batch_size: int,
    thinking_budget: Optional[int] = None,
    callback_url: Optional[str] = None
):
    """Background task để xử lý dịch thuật"""
    job = jobs[session_id]
    job.status = "processing"
    job.callbackUrl = callback_url
    
    # Khởi tạo API key usage tracking
    for key in api_keys:
        job.apiKeyUsage[key] = {
            "maskedKey": mask_api_key(key),
            "requestCount": 0,
            "successCount": 0,
            "failureCount": 0
        }
    
    # Chia thành batches
    batches = []
    for i in range(0, len(lines), batch_size):
        batches.append(lines[i:i + batch_size])
    
    thinking_info = f", thinking={thinking_budget}" if thinking_budget is not None else ""
    logger.info(f"Job {session_id}: {len(lines)} lines, {len(batches)} batches{thinking_info}")
    
    async with httpx.AsyncClient() as client:
        for batch_idx, batch in enumerate(batches):
            success = False
            current_key_idx = 0
            retries = 0
            
            while not success and retries < config.max_retries:
                # Lấy API key hiện tại
                api_key = api_keys[current_key_idx % len(api_keys)]
                
                # Chờ rate limit (trừ khi là lỗi invalid key)
                await wait_for_rate_limit()
                
                logger.info(f"Job {session_id}: Batch {batch_idx + 1}/{len(batches)}, "
                           f"key #{current_key_idx % len(api_keys) + 1}, retry {retries}")
                
                success, result, try_another_key = await call_gemini_api(
                    client, api_key, model, prompt, system_instruction, batch, thinking_budget
                )
                
                # Track API key usage
                job.apiKeyUsage[api_key]["requestCount"] += 1
                
                if success:
                    # Track success
                    job.apiKeyUsage[api_key]["successCount"] += 1
                    
                    # Parse kết quả và thêm vào results
                    parsed_results = parse_translation_result(result, batch)
                    job.results.extend(parsed_results)
                    job.completedLines += len(batch)
                    job.progress = job.completedLines / job.totalLines * 100
                    logger.info(f"Job {session_id}: Batch {batch_idx + 1} completed")
                else:
                    # Track failure
                    job.apiKeyUsage[api_key]["failureCount"] += 1
                    
                    logger.warning(f"Job {session_id}: Batch {batch_idx + 1} failed: {result}")
                    
                    if try_another_key:
                        # Thử key khác
                        current_key_idx += 1
                        if current_key_idx >= len(api_keys) * 2:  # Đã thử tất cả key 2 lần
                            retries += 1
                            current_key_idx = 0
                            # Delay trước khi retry
                            delay = config.retry_delay_base * (2 ** retries)
                            logger.info(f"Job {session_id}: Waiting {delay}s before retry")
                            await asyncio.sleep(delay)
                    else:
                        retries += 1
                        delay = config.retry_delay_base * (2 ** retries)
                        await asyncio.sleep(delay)
            
            if not success:
                job.status = "failed"
                job.error = f"Failed to process batch {batch_idx + 1} after {config.max_retries} retries"
                logger.error(f"Job {session_id}: {job.error}")
                
                # Gửi callback khi failed
                if callback_url:
                    await send_job_callback(job)
                return
    
    job.status = "completed"
    job.progress = 100
    logger.info(f"Job {session_id}: Completed successfully")
    
    # Gửi callback khi completed
    if callback_url:
        await send_job_callback(job)

async def send_job_callback(job: JobStatus):
    """Gửi callback với kết quả và thống kê API key usage"""
    if not job.callbackUrl:
        return
    
    # Chuẩn bị API key usage summary
    api_key_stats = []
    for api_key, usage in job.apiKeyUsage.items():
        api_key_stats.append({
            "apiKey": api_key,
            "maskedKey": usage["maskedKey"],
            "requestCount": usage["requestCount"],
            "successCount": usage["successCount"],
            "failureCount": usage["failureCount"]
        })
    
    callback_payload = {
        "sessionId": job.sessionId,
        "status": job.status,
        "totalLines": job.totalLines,
        "completedLines": job.completedLines,
        "error": job.error,
        "apiKeyUsage": api_key_stats,
        "totalRequests": sum(u["requestCount"] for u in job.apiKeyUsage.values()),
        "totalSuccess": sum(u["successCount"] for u in job.apiKeyUsage.values()),
        "totalFailure": sum(u["failureCount"] for u in job.apiKeyUsage.values())
    }
    
    logger.info(f"Sending callback for job {job.sessionId} to {job.callbackUrl}")
    await send_callback(job.callbackUrl, callback_payload)

def parse_translation_result(result: str, original_batch: list[SubtitleLine]) -> list[TranslatedLine]:
    """Parse kết quả dịch từ Gemini"""
    translated_lines = []
    result_lines = result.strip().split("\n")
    
    # Tạo map từ index -> original text
    original_map = {line.index: line.text for line in original_batch}
    
    for result_line in result_lines:
        result_line = result_line.strip()
        if not result_line:
            continue
        
        # Expect format: index|translated_text
        if "|" in result_line:
            parts = result_line.split("|", 1)
            try:
                idx = int(parts[0].strip())
                translated_text = parts[1].strip() if len(parts) > 1 else ""
                
                if idx in original_map:
                    translated_lines.append(TranslatedLine(
                        index=idx,
                        original=original_map[idx],
                        translated=translated_text
                    ))
            except ValueError:
                continue
    
    # Nếu parse không đủ, thêm các dòng còn thiếu với placeholder
    parsed_indices = {line.index for line in translated_lines}
    for original in original_batch:
        if original.index not in parsed_indices:
            translated_lines.append(TranslatedLine(
                index=original.index,
                original=original.text,
                translated=f"[PARSE_ERROR] {original.text}"
            ))
    
    # Sắp xếp theo index
    translated_lines.sort(key=lambda x: x.index)
    return translated_lines

# ============== FASTAPI APP ==============

@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("Subtitle Translation Server starting...")
    yield
    logger.info("Subtitle Translation Server shutting down...")

app = FastAPI(
    title="Subtitle Translation Server",
    description="Internal server for subtitle translation using Gemini API",
    version="1.0.0",
    lifespan=lifespan
)

@app.get("/")
async def root():
    return {
        "service": "Subtitle Translation Server",
        "status": "running",
        "config": {
            "rpm": config.rpm,
            "maxRetries": config.max_retries
        },
        "activeJobs": len([j for j in jobs.values() if j.status == "processing"]),
        "totalJobs": len(jobs)
    }

@app.post("/translate")
async def submit_translation(request: TranslationRequest, background_tasks: BackgroundTasks):
    """Submit job dịch thuật mới"""
    session_id = request.sessionId
    
    # Kiểm tra sessionId đã tồn tại
    if session_id in jobs:
        existing = jobs[session_id]
        if existing.status in ["processing", "pending"]:
            raise HTTPException(
                status_code=400,
                detail=f"Job {session_id} is already {existing.status}"
            )
    
    # Kiểm tra API keys
    if not request.apiKeys:
        raise HTTPException(status_code=400, detail="No API keys provided")
    
    # Tạo job mới
    job = JobStatus(
        sessionId=session_id,
        status="pending",
        progress=0,
        totalLines=len(request.lines),
        completedLines=0,
        results=[]
    )
    jobs[session_id] = job
    
    # Bắt đầu background task
    background_tasks.add_task(
        process_translation_job,
        session_id,
        request.model,
        request.prompt,
        request.systemInstruction,
        request.lines,
        request.apiKeys,
        request.batchSize,
        request.thinkingBudget,
        request.callbackUrl
    )
    
    thinking_info = f", thinking={request.thinkingBudget}" if request.thinkingBudget is not None else ""
    callback_info = f", callback={request.callbackUrl}" if request.callbackUrl else ""
    logger.info(f"Job {session_id} submitted: {len(request.lines)} lines{thinking_info}{callback_info}")
    
    return {
        "sessionId": session_id,
        "status": "pending",
        "totalLines": len(request.lines),
        "batchSize": request.batchSize,
        "thinkingBudget": request.thinkingBudget,
        "callbackUrl": request.callbackUrl,
        "message": "Job submitted successfully"
    }

@app.get("/status/{session_id}")
async def get_status(session_id: str):
    """Polling endpoint để lấy trạng thái và kết quả"""
    if session_id not in jobs:
        raise HTTPException(status_code=404, detail=f"Job {session_id} not found")
    
    job = jobs[session_id]
    
    # Chuẩn bị API key usage (masked)
    api_key_stats = []
    for api_key, usage in job.apiKeyUsage.items():
        api_key_stats.append({
            "maskedKey": usage["maskedKey"],
            "requestCount": usage["requestCount"],
            "successCount": usage["successCount"],
            "failureCount": usage["failureCount"]
        })
    
    return {
        "sessionId": job.sessionId,
        "status": job.status,
        "progress": round(job.progress, 2),
        "totalLines": job.totalLines,
        "completedLines": job.completedLines,
        "results": [r.model_dump() for r in job.results] if job.status == "completed" else [],
        "error": job.error,
        "apiKeyUsage": api_key_stats,
        "totalRequests": sum(u["requestCount"] for u in job.apiKeyUsage.values())
    }

@app.get("/results/{session_id}")
async def get_results(session_id: str):
    """Lấy kết quả đầy đủ (chỉ khi completed)"""
    if session_id not in jobs:
        raise HTTPException(status_code=404, detail=f"Job {session_id} not found")
    
    job = jobs[session_id]
    
    # Chuẩn bị API key usage (masked cho security)
    api_key_stats = []
    for api_key, usage in job.apiKeyUsage.items():
        api_key_stats.append({
            "maskedKey": usage["maskedKey"],
            "requestCount": usage["requestCount"],
            "successCount": usage["successCount"],
            "failureCount": usage["failureCount"]
        })
    
    if job.status != "completed":
        return {
            "sessionId": job.sessionId,
            "status": job.status,
            "message": "Job not completed yet",
            "progress": round(job.progress, 2),
            "apiKeyUsage": api_key_stats
        }
    
    # Sắp xếp kết quả theo index
    sorted_results = sorted(job.results, key=lambda x: x.index)
    
    return {
        "sessionId": job.sessionId,
        "status": "completed",
        "totalLines": job.totalLines,
        "results": [r.model_dump() for r in sorted_results],
        "apiKeyUsage": api_key_stats,
        "totalRequests": sum(u["requestCount"] for u in job.apiKeyUsage.values()),
        "totalSuccess": sum(u["successCount"] for u in job.apiKeyUsage.values()),
        "totalFailure": sum(u["failureCount"] for u in job.apiKeyUsage.values())
    }

@app.post("/config")
async def update_config(update: ConfigUpdate):
    """Cập nhật cấu hình server"""
    changes = []
    
    if update.rpm is not None:
        old_rpm = config.rpm
        config.rpm = max(1, update.rpm)  # Tối thiểu 1 RPM
        changes.append(f"RPM: {old_rpm} -> {config.rpm}")
    
    if update.maxRetries is not None:
        old_retries = config.max_retries
        config.max_retries = max(1, update.maxRetries)
        changes.append(f"Max retries: {old_retries} -> {config.max_retries}")
    
    logger.info(f"Config updated: {', '.join(changes)}")
    
    return {
        "success": True,
        "changes": changes,
        "currentConfig": {
            "rpm": config.rpm,
            "maxRetries": config.max_retries
        }
    }

@app.get("/config")
async def get_config():
    """Lấy cấu hình hiện tại"""
    return {
        "rpm": config.rpm,
        "maxRetries": config.max_retries,
        "retryDelayBase": config.retry_delay_base
    }

@app.delete("/job/{session_id}")
async def delete_job(session_id: str):
    """Xóa job khỏi memory"""
    if session_id not in jobs:
        raise HTTPException(status_code=404, detail=f"Job {session_id} not found")
    
    job = jobs[session_id]
    if job.status == "processing":
        raise HTTPException(status_code=400, detail="Cannot delete a processing job")
    
    del jobs[session_id]
    return {"success": True, "message": f"Job {session_id} deleted"}

@app.get("/jobs")
async def list_jobs():
    """Liệt kê tất cả jobs"""
    return {
        "total": len(jobs),
        "jobs": [
            {
                "sessionId": j.sessionId,
                "status": j.status,
                "progress": round(j.progress, 2),
                "totalLines": j.totalLines
            }
            for j in jobs.values()
        ]
    }

@app.delete("/jobs/completed")
async def cleanup_completed_jobs():
    """Xóa tất cả jobs đã hoàn thành"""
    completed = [sid for sid, job in jobs.items() if job.status in ["completed", "failed"]]
    for sid in completed:
        del jobs[sid]
    
    return {"deleted": len(completed), "remaining": len(jobs)}

# ============== MAIN ==============

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8080)
