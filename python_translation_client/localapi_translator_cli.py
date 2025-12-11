#!/usr/bin/env python3
"""
LocalAPI Translation Client - Command Line Version
Ứng dụng dịch văn bản hàng loạt sử dụng LocalAPI của SubPhim Server.

Phiên bản command-line, không cần GUI (tkinter).

QUAN TRỌNG: Chương trình này KHÔNG sử dụng endpoint Admin/VipTranslation.
Chỉ sử dụng endpoint /api/launcheraio (LocalAPI).
"""

import os
import sys
import json
import time
import hashlib
import argparse
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from typing import Optional, Dict, List, Tuple
from datetime import datetime

import requests

# Cấu hình logging
import logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


@dataclass
class ServerConfig:
    """Cấu hình server"""
    base_url: str = "http://localhost:5000"
    timeout: int = 60
    max_concurrent_sessions: int = 100


@dataclass
class UserSession:
    """Thông tin phiên đăng nhập"""
    token: str
    user_id: int
    username: str
    local_srt_lines_used: int
    daily_local_srt_limit: int


class AuthService:
    """Service xử lý đăng ký/đăng nhập"""
    
    def __init__(self, config: ServerConfig):
        self.config = config
        self.session: Optional[UserSession] = None
    
    def _get_hwid(self) -> str:
        """Tạo HWID dựa trên thông tin máy tính"""
        import platform
        import uuid
        
        machine_info = f"{platform.node()}-{uuid.getnode()}"
        return hashlib.md5(machine_info.encode()).hexdigest()
    
    def register(self, username: str, password: str, email: str) -> Tuple[bool, str]:
        """Đăng ký tài khoản mới"""
        url = f"{self.config.base_url}/api/auth/register"
        payload = {
            "username": username,
            "password": password,
            "email": email,
            "hwid": self._get_hwid()
        }
        
        try:
            response = requests.post(url, json=payload, timeout=self.config.timeout)
            
            if response.status_code == 200:
                return True, "Đăng ký thành công!"
            else:
                error_msg = response.text
                return False, f"Lỗi đăng ký: {error_msg}"
        except requests.RequestException as e:
            return False, f"Lỗi kết nối: {str(e)}"
    
    def login(self, username: str, password: str) -> Tuple[bool, str]:
        """Đăng nhập"""
        url = f"{self.config.base_url}/api/auth/login"
        payload = {
            "username": username,
            "password": password,
            "hwid": self._get_hwid()
        }
        
        try:
            response = requests.post(url, json=payload, timeout=self.config.timeout)
            
            if response.status_code == 200:
                data = response.json()
                self.session = UserSession(
                    token=data.get('token'),
                    user_id=data.get('id'),
                    username=data.get('username'),
                    local_srt_lines_used=data.get('localSrtLinesUsedToday', 0),
                    daily_local_srt_limit=data.get('dailyLocalSrtLineLimit', 0)
                )
                return True, "Đăng nhập thành công!"
            else:
                error_msg = response.text
                return False, f"Lỗi đăng nhập: {error_msg}"
        except requests.RequestException as e:
            return False, f"Lỗi kết nối: {str(e)}"
    
    def get_auth_headers(self) -> Dict[str, str]:
        """Lấy headers xác thực"""
        if not self.session:
            raise ValueError("Chưa đăng nhập")
        return {
            "Authorization": f"Bearer {self.session.token}",
            "Content-Type": "application/json"
        }
    
    def is_authenticated(self) -> bool:
        """Kiểm tra đã đăng nhập chưa"""
        return self.session is not None


class TranslationService:
    """
    Service dịch văn bản sử dụng LocalAPI.
    
    QUAN TRỌNG: 
    - CHỈ sử dụng endpoint /api/launcheraio (LocalAPI)
    - KHÔNG sử dụng endpoint /api/viptranslation (đang test, không được gọi)
    """
    
    def __init__(self, config: ServerConfig, auth_service: AuthService):
        self.config = config
        self.auth = auth_service
        self.active_sessions: Dict[str, dict] = {}
        self._lock = threading.Lock()
    
    def start_translation(self, content: str, system_instruction: str, 
                         target_language: str = "Vietnamese") -> Tuple[bool, str, Optional[str]]:
        """
        Bắt đầu job dịch.
        
        Endpoint: POST /api/launcheraio/start-translation
        
        Returns:
            Tuple[success, message, session_id]
        """
        url = f"{self.config.base_url}/api/launcheraio/start-translation"
        
        # Tạo một SrtLine duy nhất với Index=1 và OriginalText là toàn bộ nội dung file txt
        srt_lines = [{
            "index": 1,
            "originalText": content
        }]
        
        payload = {
            "genre": "Custom",
            "targetLanguage": target_language,
            "lines": srt_lines,
            "systemInstruction": system_instruction,
            "acceptPartial": True
        }
        
        try:
            response = requests.post(
                url, 
                json=payload,
                headers=self.auth.get_auth_headers(),
                timeout=self.config.timeout
            )
            
            data = response.json()
            status = data.get('status', '')
            
            if response.status_code == 200 and status == 'Accepted':
                session_id = data.get('sessionId')
                with self._lock:
                    self.active_sessions[session_id] = {
                        'status': 'pending',
                        'started_at': time.time()
                    }
                return True, "Job đã được chấp nhận", session_id
            
            elif response.status_code == 202:
                remaining = data.get('remainingLines', 0)
                return False, f"Chỉ còn {remaining} lượt dịch", None
            
            elif response.status_code == 429:
                return False, data.get('message', 'Đã hết lượt dịch'), None
            
            else:
                return False, data.get('message', f'Lỗi: {response.status_code}'), None
                
        except requests.RequestException as e:
            return False, f"Lỗi kết nối: {str(e)}", None
    
    def poll_results(self, session_id: str, max_attempts: int = 120, 
                    interval: float = 1.0) -> Tuple[bool, str, Optional[str]]:
        """
        Polling lấy kết quả dịch.
        
        Endpoint: GET /api/launcheraio/get-results/{sessionId}
        """
        url = f"{self.config.base_url}/api/launcheraio/get-results/{session_id}"
        
        for attempt in range(max_attempts):
            try:
                response = requests.get(
                    url,
                    headers=self.auth.get_auth_headers(),
                    timeout=self.config.timeout
                )
                
                if response.status_code == 404:
                    return False, "Session không tồn tại", None
                
                if response.status_code != 200:
                    time.sleep(interval)
                    continue
                
                data = response.json()
                is_completed = data.get('isCompleted', False)
                error_message = data.get('errorMessage')
                new_lines = data.get('newLines', [])
                
                if is_completed:
                    with self._lock:
                        if session_id in self.active_sessions:
                            del self.active_sessions[session_id]
                    
                    if error_message:
                        return False, error_message, error_message
                    
                    if new_lines and len(new_lines) > 0:
                        first_line = new_lines[0]
                        translated_text = first_line.get('translatedText', '')
                        success = first_line.get('success', False)
                        
                        if success and translated_text:
                            return True, translated_text, None
                        else:
                            return False, translated_text or "Không có kết quả", None
                    
                    return False, "Kết quả trống", None
                
                logger.debug(f"Session {session_id}: Đang xử lý... ({attempt + 1}/{max_attempts})")
                time.sleep(interval)
                
            except requests.RequestException as e:
                logger.warning(f"Poll error: {e}")
                time.sleep(interval)
        
        with self._lock:
            if session_id in self.active_sessions:
                del self.active_sessions[session_id]
        return False, "Timeout", None
    
    def translate_file(self, file_path: str, system_instruction: str,
                      target_language: str = "Vietnamese") -> Tuple[bool, str, Optional[str]]:
        """Dịch một file txt."""
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read().strip()
            
            if not content:
                return False, "File rỗng", None
            
            logger.info(f"Đang gửi: {os.path.basename(file_path)}")
            
            success, message, session_id = self.start_translation(
                content, system_instruction, target_language
            )
            
            if not success:
                return False, message, None
            
            logger.info(f"Session {session_id}: Đang chờ kết quả...")
            
            success, result, error = self.poll_results(session_id)
            
            if success:
                first_line = result.split('\n')[0][:50].strip()
                invalid_chars = '<>:"/\\|?*'
                for char in invalid_chars:
                    first_line = first_line.replace(char, '')
                
                suggested_filename = first_line or "translated"
                return True, result, suggested_filename
            else:
                return False, result, None
                
        except Exception as e:
            logger.error(f"Error: {file_path}: {e}")
            return False, str(e), None


def translate_batch(folder: str, output_folder: str, system_instruction: str,
                   target_language: str, max_concurrent: int,
                   auth_service: AuthService, config: ServerConfig):
    """Dịch hàng loạt các file trong thư mục"""
    
    translator = TranslationService(config, auth_service)
    
    txt_files = [f for f in os.listdir(folder) if f.endswith('.txt')]
    if not txt_files:
        logger.error("Không tìm thấy file .txt")
        return
    
    os.makedirs(output_folder, exist_ok=True)
    
    total_files = len(txt_files)
    success_count = 0
    failed_count = 0
    
    logger.info(f"Bắt đầu dịch {total_files} files với {max_concurrent} sessions đồng thời")
    
    def translate_single(filename: str) -> Tuple[str, bool, str]:
        file_path = os.path.join(folder, filename)
        success, result, suggested_name = translator.translate_file(
            file_path, system_instruction, target_language
        )
        
        if success:
            output_filename = f"{suggested_name}.txt" if suggested_name else f"{os.path.splitext(filename)[0]}_translated.txt"
            output_filename = "".join(c for c in output_filename if c.isalnum() or c in ' ._-').strip()
            if not output_filename:
                output_filename = f"{os.path.splitext(filename)[0]}_translated.txt"
            
            output_path = os.path.join(output_folder, output_filename)
            
            counter = 1
            while os.path.exists(output_path):
                name, ext = os.path.splitext(output_filename)
                output_path = os.path.join(output_folder, f"{name}_{counter}{ext}")
                counter += 1
            
            with open(output_path, 'w', encoding='utf-8') as f:
                f.write(result)
            
            return filename, True, output_path
        else:
            return filename, False, result
    
    with ThreadPoolExecutor(max_workers=max_concurrent) as executor:
        futures = {executor.submit(translate_single, f): f for f in txt_files}
        
        for future in as_completed(futures):
            filename, success, result = future.result()
            
            if success:
                success_count += 1
                logger.info(f"✓ {filename} -> {os.path.basename(result)}")
            else:
                failed_count += 1
                logger.error(f"✗ {filename}: {result}")
    
    logger.info(f"\n=== Hoàn thành ===")
    logger.info(f"Thành công: {success_count}/{total_files}")
    logger.info(f"Thất bại: {failed_count}/{total_files}")
    logger.info(f"Output: {output_folder}")


def main():
    """Entry point cho command line"""
    parser = argparse.ArgumentParser(
        description='LocalAPI Translation Client - Dịch văn bản hàng loạt',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog='''
Ví dụ sử dụng:
  # Đăng ký tài khoản mới
  python localapi_translator_cli.py --server http://localhost:5000 --register --username user1 --password pass123 --email user1@example.com

  # Đăng nhập và dịch
  python localapi_translator_cli.py --server http://localhost:5000 --username user1 --password pass123 --folder ./texts --instruction "Dịch sang tiếng Việt"

QUAN TRỌNG: Chương trình này KHÔNG sử dụng endpoint Admin/VipTranslation.
        '''
    )
    
    parser.add_argument('--server', default='http://localhost:5000', help='URL của server')
    parser.add_argument('--username', required=True, help='Tên đăng nhập')
    parser.add_argument('--password', required=True, help='Mật khẩu')
    parser.add_argument('--email', help='Email (chỉ cần khi đăng ký)')
    parser.add_argument('--register', action='store_true', help='Đăng ký tài khoản mới')
    
    parser.add_argument('--folder', help='Thư mục chứa file .txt cần dịch')
    parser.add_argument('--output', help='Thư mục output (mặc định: [folder]/Đã dịch)')
    parser.add_argument('--instruction', default='Dịch văn bản sau sang tiếng Việt, giữ nguyên format.', 
                       help='System instruction cho AI')
    parser.add_argument('--language', default='Vietnamese', help='Ngôn ngữ đích')
    parser.add_argument('--concurrent', type=int, default=10, help='Số session đồng thời (tối đa 100)')
    
    args = parser.parse_args()
    
    # Cấu hình
    config = ServerConfig(base_url=args.server.rstrip('/'))
    auth = AuthService(config)
    
    # Đăng ký nếu cần
    if args.register:
        if not args.email:
            parser.error("--email bắt buộc khi đăng ký")
        success, message = auth.register(args.username, args.password, args.email)
        logger.info(message)
        if not success:
            sys.exit(1)
        return
    
    # Đăng nhập
    success, message = auth.login(args.username, args.password)
    logger.info(message)
    if not success:
        sys.exit(1)
    
    logger.info(f"Quota: {auth.session.local_srt_lines_used}/{auth.session.daily_local_srt_limit}")
    
    # Dịch nếu có folder
    if args.folder:
        if not os.path.isdir(args.folder):
            logger.error(f"Thư mục không tồn tại: {args.folder}")
            sys.exit(1)
        
        output_folder = args.output or os.path.join(args.folder, "Đã dịch")
        max_concurrent = min(args.concurrent, 100)
        
        translate_batch(
            args.folder, output_folder, args.instruction,
            args.language, max_concurrent, auth, config
        )
    else:
        logger.info("Đăng nhập thành công. Sử dụng --folder để dịch file.")


if __name__ == "__main__":
    main()
