#!/usr/bin/env python3
"""
LocalAPI Translation Client
Ứng dụng dịch văn bản hàng loạt sử dụng LocalAPI của SubPhim Server.

QUAN TRỌNG: Chương trình này KHÔNG sử dụng endpoint Admin/VipTranslation.
Chỉ sử dụng endpoint /api/launcheraio (LocalAPI).
"""

import os
import sys
import json
import time
import queue
import logging
import hashlib
import threading
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, scrolledtext
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from typing import Optional, Dict, List, Tuple
from datetime import datetime

import requests

# Cấu hình logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


@dataclass
class ServerConfig:
    """Cấu hình server"""
    base_url: str = "http://localhost:5000"  # Thay đổi URL server thực tế
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
        
        QUAN TRỌNG: Nội dung file txt được gửi như một dòng SRT duy nhất để bypass 
        cấu trúc server cần (Index + OriginalText).
        
        Returns:
            Tuple[success, message, session_id]
        """
        url = f"{self.config.base_url}/api/launcheraio/start-translation"
        
        # Tạo một SrtLine duy nhất với Index=1 và OriginalText là toàn bộ nội dung file txt
        # Đây là cách bypass để server xử lý văn bản dài thay vì từng dòng SRT
        srt_lines = [{
            "index": 1,
            "originalText": content  # Toàn bộ nội dung file txt là một "dòng SRT"
        }]
        
        payload = {
            "genre": "Custom",  # Thể loại tùy chỉnh
            "targetLanguage": target_language,
            "lines": srt_lines,
            "systemInstruction": system_instruction,
            "acceptPartial": True  # Chấp nhận dịch một phần nếu hết quota
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
                        'result': None,
                        'error': None,
                        'started_at': time.time()
                    }
                return True, "Job đã được chấp nhận", session_id
            
            elif response.status_code == 202:
                # Partial content - server yêu cầu xác nhận
                remaining = data.get('remainingLines', 0)
                return False, f"Chỉ còn {remaining} lượt dịch, cần xác nhận dịch một phần", None
            
            elif response.status_code == 429:
                # Hết quota
                return False, data.get('message', 'Đã hết lượt dịch trong ngày'), None
            
            else:
                return False, data.get('message', f'Lỗi không xác định: {response.status_code}'), None
                
        except requests.RequestException as e:
            return False, f"Lỗi kết nối: {str(e)}", None
    
    def poll_results(self, session_id: str, max_attempts: int = 120, 
                    interval: float = 1.0) -> Tuple[bool, str, Optional[str]]:
        """
        Polling lấy kết quả dịch.
        
        Endpoint: GET /api/launcheraio/get-results/{sessionId}
        
        Returns:
            Tuple[success, translated_text_or_error, error_message]
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
                    return False, "Session không tồn tại hoặc đã hết hạn", None
                
                if response.status_code != 200:
                    logger.warning(f"Poll attempt {attempt + 1}: HTTP {response.status_code}")
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
                    
                    # Lấy kết quả dịch từ dòng đầu tiên (vì chúng ta chỉ gửi 1 dòng SRT)
                    if new_lines and len(new_lines) > 0:
                        first_line = new_lines[0]
                        translated_text = first_line.get('translatedText', '')
                        success = first_line.get('success', False)
                        
                        if success and translated_text:
                            return True, translated_text, None
                        else:
                            return False, translated_text or "Không có kết quả dịch", None
                    
                    return False, "Kết quả dịch trống", None
                
                # Job chưa hoàn thành, tiếp tục polling
                logger.debug(f"Session {session_id}: Đang xử lý... (attempt {attempt + 1}/{max_attempts})")
                time.sleep(interval)
                
            except requests.RequestException as e:
                logger.warning(f"Poll error for session {session_id}: {e}")
                time.sleep(interval)
        
        # Timeout
        with self._lock:
            if session_id in self.active_sessions:
                del self.active_sessions[session_id]
        return False, "Timeout: Không nhận được kết quả sau nhiều lần polling", None
    
    def translate_file(self, file_path: str, system_instruction: str,
                      target_language: str = "Vietnamese",
                      callback=None) -> Tuple[bool, str, Optional[str]]:
        """
        Dịch một file txt.
        
        Returns:
            Tuple[success, translated_text_or_error, suggested_filename]
        """
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read().strip()
            
            if not content:
                return False, "File rỗng", None
            
            if callback:
                callback(f"Đang gửi file: {os.path.basename(file_path)}")
            
            # Bắt đầu dịch
            success, message, session_id = self.start_translation(
                content, system_instruction, target_language
            )
            
            if not success:
                return False, message, None
            
            if callback:
                callback(f"Session {session_id}: Đang chờ kết quả...")
            
            # Polling kết quả
            success, result, error = self.poll_results(session_id)
            
            if success:
                # Tạo tên file từ dòng đầu tiên của kết quả
                first_line = result.split('\n')[0][:50].strip()
                # Loại bỏ các ký tự không hợp lệ cho tên file
                invalid_chars = '<>:"/\\|?*'
                for char in invalid_chars:
                    first_line = first_line.replace(char, '')
                
                suggested_filename = first_line or "translated"
                return True, result, suggested_filename
            else:
                return False, result, None
                
        except Exception as e:
            logger.error(f"Error translating file {file_path}: {e}")
            return False, str(e), None


class TranslationApp:
    """Ứng dụng GUI dịch văn bản hàng loạt"""
    
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("LocalAPI Translation Client")
        self.root.geometry("900x700")
        self.root.minsize(800, 600)
        
        # Services
        self.config = ServerConfig()
        self.auth = AuthService(self.config)
        self.translator: Optional[TranslationService] = None
        
        # State
        self.is_translating = False
        self.stop_requested = False
        self.executor: Optional[ThreadPoolExecutor] = None
        self.log_queue = queue.Queue()
        
        # UI
        self._create_ui()
        self._update_log()
    
    def _create_ui(self):
        """Tạo giao diện người dùng"""
        # Main container
        main_frame = ttk.Frame(self.root, padding="10")
        main_frame.grid(row=0, column=0, sticky="nsew")
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        main_frame.columnconfigure(0, weight=1)
        
        # === Server Configuration ===
        server_frame = ttk.LabelFrame(main_frame, text="Cấu hình Server", padding="5")
        server_frame.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        server_frame.columnconfigure(1, weight=1)
        
        ttk.Label(server_frame, text="Server URL:").grid(row=0, column=0, sticky="w", padx=5)
        self.server_url_entry = ttk.Entry(server_frame, width=60)
        self.server_url_entry.grid(row=0, column=1, sticky="ew", padx=5)
        self.server_url_entry.insert(0, "http://localhost:5000")  # URL mặc định
        
        ttk.Button(server_frame, text="Cập nhật", command=self._update_server_url).grid(
            row=0, column=2, padx=5
        )
        
        # === Authentication Frame ===
        auth_frame = ttk.LabelFrame(main_frame, text="Đăng nhập / Đăng ký", padding="5")
        auth_frame.grid(row=1, column=0, sticky="ew", pady=(0, 10))
        
        ttk.Label(auth_frame, text="Username:").grid(row=0, column=0, sticky="w", padx=5)
        self.username_entry = ttk.Entry(auth_frame, width=20)
        self.username_entry.grid(row=0, column=1, padx=5)
        
        ttk.Label(auth_frame, text="Password:").grid(row=0, column=2, sticky="w", padx=5)
        self.password_entry = ttk.Entry(auth_frame, width=20, show="*")
        self.password_entry.grid(row=0, column=3, padx=5)
        
        ttk.Label(auth_frame, text="Email (đăng ký):").grid(row=0, column=4, sticky="w", padx=5)
        self.email_entry = ttk.Entry(auth_frame, width=25)
        self.email_entry.grid(row=0, column=5, padx=5)
        
        btn_frame = ttk.Frame(auth_frame)
        btn_frame.grid(row=1, column=0, columnspan=6, pady=5)
        
        ttk.Button(btn_frame, text="Đăng nhập", command=self._login).pack(side="left", padx=5)
        ttk.Button(btn_frame, text="Đăng ký", command=self._register).pack(side="left", padx=5)
        
        self.auth_status_label = ttk.Label(auth_frame, text="Chưa đăng nhập", foreground="red")
        self.auth_status_label.grid(row=2, column=0, columnspan=6, pady=5)
        
        # === Translation Settings ===
        settings_frame = ttk.LabelFrame(main_frame, text="Cài đặt dịch", padding="5")
        settings_frame.grid(row=2, column=0, sticky="ew", pady=(0, 10))
        settings_frame.columnconfigure(1, weight=1)
        
        # System Instruction
        ttk.Label(settings_frame, text="System Instruction:").grid(row=0, column=0, sticky="nw", padx=5)
        self.instruction_text = scrolledtext.ScrolledText(settings_frame, width=70, height=5)
        self.instruction_text.grid(row=0, column=1, sticky="ew", padx=5, pady=5)
        self.instruction_text.insert("1.0", """Bạn là một dịch giả chuyên nghiệp. Hãy dịch văn bản sau sang tiếng Việt.
Giữ nguyên format và bố cục của văn bản gốc.
Dịch tự nhiên, đúng ngữ cảnh, không dịch máy móc.""")
        
        # Target Language
        ttk.Label(settings_frame, text="Ngôn ngữ đích:").grid(row=1, column=0, sticky="w", padx=5)
        self.target_lang_entry = ttk.Entry(settings_frame, width=20)
        self.target_lang_entry.grid(row=1, column=1, sticky="w", padx=5)
        self.target_lang_entry.insert(0, "Vietnamese")
        
        # Max concurrent sessions
        ttk.Label(settings_frame, text="Số session đồng thời (tối đa 100):").grid(row=2, column=0, sticky="w", padx=5)
        self.concurrent_var = tk.IntVar(value=10)
        concurrent_spinbox = ttk.Spinbox(settings_frame, from_=1, to=100, 
                                         textvariable=self.concurrent_var, width=10)
        concurrent_spinbox.grid(row=2, column=1, sticky="w", padx=5)
        
        # === Folder Selection ===
        folder_frame = ttk.LabelFrame(main_frame, text="Thư mục dịch", padding="5")
        folder_frame.grid(row=3, column=0, sticky="ew", pady=(0, 10))
        folder_frame.columnconfigure(1, weight=1)
        
        ttk.Label(folder_frame, text="Thư mục chứa file .txt:").grid(row=0, column=0, sticky="w", padx=5)
        self.folder_entry = ttk.Entry(folder_frame, width=60)
        self.folder_entry.grid(row=0, column=1, sticky="ew", padx=5)
        ttk.Button(folder_frame, text="Chọn...", command=self._select_folder).grid(row=0, column=2, padx=5)
        
        self.file_count_label = ttk.Label(folder_frame, text="")
        self.file_count_label.grid(row=1, column=0, columnspan=3, sticky="w", padx=5)
        
        # === Action Buttons ===
        action_frame = ttk.Frame(main_frame)
        action_frame.grid(row=4, column=0, pady=10)
        
        self.start_btn = ttk.Button(action_frame, text="Bắt đầu dịch", command=self._start_translation)
        self.start_btn.pack(side="left", padx=5)
        
        self.stop_btn = ttk.Button(action_frame, text="Dừng", command=self._stop_translation, state="disabled")
        self.stop_btn.pack(side="left", padx=5)
        
        # === Progress ===
        progress_frame = ttk.LabelFrame(main_frame, text="Tiến trình", padding="5")
        progress_frame.grid(row=5, column=0, sticky="ew", pady=(0, 10))
        progress_frame.columnconfigure(0, weight=1)
        
        self.progress_var = tk.DoubleVar(value=0)
        self.progress_bar = ttk.Progressbar(progress_frame, variable=self.progress_var, maximum=100)
        self.progress_bar.grid(row=0, column=0, sticky="ew", padx=5, pady=5)
        
        self.progress_label = ttk.Label(progress_frame, text="0/0 files")
        self.progress_label.grid(row=1, column=0, sticky="w", padx=5)
        
        # === Log ===
        log_frame = ttk.LabelFrame(main_frame, text="Log", padding="5")
        log_frame.grid(row=6, column=0, sticky="nsew", pady=(0, 10))
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)
        main_frame.rowconfigure(6, weight=1)
        
        self.log_text = scrolledtext.ScrolledText(log_frame, height=15, state="disabled")
        self.log_text.grid(row=0, column=0, sticky="nsew")
        
        ttk.Button(log_frame, text="Xóa Log", command=self._clear_log).grid(row=1, column=0, sticky="e", pady=5)
    
    def _update_server_url(self):
        """Cập nhật URL server"""
        url = self.server_url_entry.get().strip()
        if url:
            self.config.base_url = url.rstrip('/')
            self.auth = AuthService(self.config)
            self.translator = None
            self._log("Server URL đã cập nhật: " + self.config.base_url)
    
    def _login(self):
        """Xử lý đăng nhập"""
        username = self.username_entry.get().strip()
        password = self.password_entry.get()
        
        if not username or not password:
            messagebox.showerror("Lỗi", "Vui lòng nhập username và password")
            return
        
        self._update_server_url()
        
        success, message = self.auth.login(username, password)
        
        if success:
            self.translator = TranslationService(self.config, self.auth)
            self.auth_status_label.config(
                text=f"Đã đăng nhập: {self.auth.session.username} | "
                     f"Quota: {self.auth.session.local_srt_lines_used}/{self.auth.session.daily_local_srt_limit}",
                foreground="green"
            )
            self._log(message)
        else:
            messagebox.showerror("Lỗi đăng nhập", message)
            self._log(f"Lỗi đăng nhập: {message}")
    
    def _register(self):
        """Xử lý đăng ký"""
        username = self.username_entry.get().strip()
        password = self.password_entry.get()
        email = self.email_entry.get().strip()
        
        if not username or not password or not email:
            messagebox.showerror("Lỗi", "Vui lòng nhập đầy đủ thông tin")
            return
        
        self._update_server_url()
        
        success, message = self.auth.register(username, password, email)
        
        if success:
            messagebox.showinfo("Thành công", message)
            self._log(message)
        else:
            messagebox.showerror("Lỗi đăng ký", message)
            self._log(f"Lỗi đăng ký: {message}")
    
    def _select_folder(self):
        """Chọn thư mục chứa file txt"""
        folder = filedialog.askdirectory(title="Chọn thư mục chứa file .txt")
        if folder:
            self.folder_entry.delete(0, tk.END)
            self.folder_entry.insert(0, folder)
            
            # Đếm số file txt
            txt_files = [f for f in os.listdir(folder) if f.endswith('.txt')]
            self.file_count_label.config(text=f"Tìm thấy {len(txt_files)} file .txt")
            self._log(f"Đã chọn thư mục: {folder} ({len(txt_files)} files)")
    
    def _log(self, message: str):
        """Thêm message vào log queue"""
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_queue.put(f"[{timestamp}] {message}")
    
    def _update_log(self):
        """Cập nhật log text từ queue (gọi từ main thread)"""
        try:
            while True:
                message = self.log_queue.get_nowait()
                self.log_text.config(state="normal")
                self.log_text.insert(tk.END, message + "\n")
                self.log_text.see(tk.END)
                self.log_text.config(state="disabled")
        except queue.Empty:
            pass
        
        self.root.after(100, self._update_log)
    
    def _clear_log(self):
        """Xóa log"""
        self.log_text.config(state="normal")
        self.log_text.delete("1.0", tk.END)
        self.log_text.config(state="disabled")
    
    def _start_translation(self):
        """Bắt đầu dịch hàng loạt"""
        if not self.auth.is_authenticated():
            messagebox.showerror("Lỗi", "Vui lòng đăng nhập trước")
            return
        
        folder = self.folder_entry.get().strip()
        if not folder or not os.path.isdir(folder):
            messagebox.showerror("Lỗi", "Vui lòng chọn thư mục hợp lệ")
            return
        
        txt_files = [f for f in os.listdir(folder) if f.endswith('.txt')]
        if not txt_files:
            messagebox.showerror("Lỗi", "Không tìm thấy file .txt trong thư mục")
            return
        
        # Tạo thư mục output
        output_folder = os.path.join(folder, "Đã dịch")
        os.makedirs(output_folder, exist_ok=True)
        
        # Lấy cài đặt
        system_instruction = self.instruction_text.get("1.0", tk.END).strip()
        target_language = self.target_lang_entry.get().strip() or "Vietnamese"
        max_concurrent = min(self.concurrent_var.get(), 100)
        
        if not system_instruction:
            messagebox.showerror("Lỗi", "Vui lòng nhập System Instruction")
            return
        
        # Cập nhật UI
        self.is_translating = True
        self.stop_requested = False
        self.start_btn.config(state="disabled")
        self.stop_btn.config(state="normal")
        self.progress_var.set(0)
        
        # Chạy trong thread riêng
        thread = threading.Thread(
            target=self._translation_worker,
            args=(folder, txt_files, output_folder, system_instruction, target_language, max_concurrent),
            daemon=True
        )
        thread.start()
    
    def _translation_worker(self, folder: str, txt_files: List[str], output_folder: str,
                           system_instruction: str, target_language: str, max_concurrent: int):
        """Worker thread thực hiện dịch"""
        total_files = len(txt_files)
        completed = 0
        success_count = 0
        failed_count = 0
        
        self._log(f"Bắt đầu dịch {total_files} files với {max_concurrent} sessions đồng thời")
        
        def translate_single_file(filename: str) -> Tuple[str, bool, str]:
            """Dịch một file và trả về (filename, success, result/error)"""
            if self.stop_requested:
                return filename, False, "Đã dừng"
            
            file_path = os.path.join(folder, filename)
            success, result, suggested_name = self.translator.translate_file(
                file_path, system_instruction, target_language,
                callback=lambda msg: self._log(f"{filename}: {msg}")
            )
            
            if success:
                # Lưu kết quả
                output_filename = f"{suggested_name}.txt" if suggested_name else f"{os.path.splitext(filename)[0]}_translated.txt"
                # Đảm bảo tên file hợp lệ
                output_filename = "".join(c for c in output_filename if c.isalnum() or c in ' ._-').strip()
                if not output_filename:
                    output_filename = f"{os.path.splitext(filename)[0]}_translated.txt"
                
                output_path = os.path.join(output_folder, output_filename)
                
                # Xử lý trùng tên file
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
            self.executor = executor
            futures = {executor.submit(translate_single_file, f): f for f in txt_files}
            
            for future in as_completed(futures):
                if self.stop_requested:
                    break
                
                filename, success, result = future.result()
                completed += 1
                
                if success:
                    success_count += 1
                    self._log(f"✓ {filename} -> {os.path.basename(result)}")
                else:
                    failed_count += 1
                    self._log(f"✗ {filename}: {result}")
                
                # Cập nhật progress
                progress = (completed / total_files) * 100
                self.root.after(0, lambda p=progress: self.progress_var.set(p))
                self.root.after(0, lambda c=completed, t=total_files: 
                               self.progress_label.config(text=f"{c}/{t} files"))
        
        self.executor = None
        
        # Kết thúc
        self._log(f"\n=== Hoàn thành ===")
        self._log(f"Thành công: {success_count}/{total_files}")
        self._log(f"Thất bại: {failed_count}/{total_files}")
        self._log(f"Output: {output_folder}")
        
        self.root.after(0, self._translation_completed)
    
    def _stop_translation(self):
        """Dừng quá trình dịch"""
        self.stop_requested = True
        self._log("Đang dừng...")
        self.stop_btn.config(state="disabled")
    
    def _translation_completed(self):
        """Được gọi khi dịch xong"""
        self.is_translating = False
        self.start_btn.config(state="normal")
        self.stop_btn.config(state="disabled")
        
        if self.stop_requested:
            messagebox.showinfo("Thông báo", "Đã dừng quá trình dịch")
        else:
            messagebox.showinfo("Hoàn thành", "Đã dịch xong tất cả files!")


def main():
    """Entry point"""
    root = tk.Tk()
    app = TranslationApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
