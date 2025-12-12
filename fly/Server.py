#!/usr/bin/env python3
"""
QuickTranslate Client for SubPhim Server
- Uses the distributed translation endpoint.
- Authenticates and then processes a single SRT file.
"""
import os
import re
import sys
import json
import time
import queue
import logging
import hashlib
import threading
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, scrolledtext
from dataclasses import dataclass
from typing import Optional, Dict, List, Tuple
from datetime import datetime
import uuid

import requests

# Cấu hình logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(threadName)s - %(message)s'
)
logger = logging.getLogger(__name__)

# ==============================================================================
# DATA CLASSES & SRT UTILITIES
# ==============================================================================

@dataclass
class ServerConfig:
    """Cấu hình server"""
    base_url: str = "http://localhost:5000"
    timeout: int = 60

@dataclass
class UserSession:
    """Thông tin phiên đăng nhập"""
    token: str
    user_id: int
    username: str
    
@dataclass
class SrtEntry:
    """Một khối trong file SRT"""
    index: int
    start_time: str
    end_time: str
    text: str

def parse_srt(content: str) -> List[SrtEntry]:
    """Phân tích nội dung file SRT thành các khối"""
    entries = []
    content = content.replace('\r\n', '\n').replace('\r', '\n')
    blocks = re.split(r'\n\n+', content.strip())
    
    for block in blocks:
        lines = block.strip().split('\n')
        if len(lines) >= 3:
            try:
                index = int(lines[0].strip())
                time_match = re.match(
                    r'(\d{2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,\.]\d{3})',
                    lines[1].strip()
                )
                if time_match:
                    start_time, end_time = time_match.groups()
                    text = '\n'.join(lines[2:]).strip()
                    entries.append(SrtEntry(index, start_time, end_time, text))
            except (ValueError, IndexError):
                logger.warning(f"Could not parse SRT block: {block}")
                continue
    return entries

def build_srt(entries: List[SrtEntry], translations: Dict[int, str]) -> str:
    """Xây dựng lại nội dung file SRT từ kết quả dịch"""
    lines = []
    for entry in entries:
        lines.append(str(entry.index))
        lines.append(f"{entry.start_time} --> {entry.end_time}")
        translated_text = translations.get(entry.index, f"[LỖI DỊCH] {entry.text}")
        lines.append(translated_text)
        lines.append("")
    return '\n'.join(lines)

# ==============================================================================
# AUTHENTICATION SERVICE (Không thay đổi)
# ==============================================================================

class AuthService:
    """Service xử lý đăng ký/đăng nhập"""
    
    def __init__(self, config: ServerConfig):
        self.config = config
        self.session: Optional[UserSession] = None
    
    def _get_hwid(self) -> str:
        """Tạo HWID dựa trên thông tin máy tính"""
        import platform
        machine_info = f"{platform.node()}-{uuid.getnode()}"
        return hashlib.md5(machine_info.encode()).hexdigest()
    
    def register(self, username: str, password: str, email: str) -> Tuple[bool, str]:
        """Đăng ký tài khoản mới"""
        url = f"{self.config.base_url}/api/auth/register"
        payload = {"username": username, "password": password, "email": email, "hwid": self._get_hwid()}
        try:
            response = requests.post(url, json=payload, timeout=self.config.timeout)
            if response.status_code == 200:
                return True, "Đăng ký thành công!"
            return False, f"Lỗi {response.status_code}: {response.text}"
        except requests.RequestException as e:
            return False, f"Lỗi kết nối: {str(e)}"
    
    def login(self, username: str, password: str) -> Tuple[bool, str]:
        """Đăng nhập"""
        url = f"{self.config.base_url}/api/auth/login"
        payload = {"username": username, "password": password, "hwid": self._get_hwid()}
        try:
            response = requests.post(url, json=payload, timeout=self.config.timeout)
            if response.status_code == 200:
                data = response.json()
                self.session = UserSession(
                    token=data.get('token'),
                    user_id=data.get('id'),
                    username=data.get('username')
                )
                return True, "Đăng nhập thành công!"
            return False, f"Lỗi {response.status_code}: {response.text}"
        except requests.RequestException as e:
            return False, f"Lỗi kết nối: {str(e)}"
    
    def get_auth_headers(self) -> Dict[str, str]:
        """Lấy headers xác thực"""
        if not self.session:
            raise ValueError("Chưa đăng nhập")
        return {"Authorization": f"Bearer {self.session.token}", "Content-Type": "application/json"}
    
    def is_authenticated(self) -> bool:
        """Kiểm tra đã đăng nhập chưa"""
        return self.session is not None

# ==============================================================================
# SUBTITLE API SERVICE (Đã chỉnh sửa để tương thích với API)
# ==============================================================================

class SubtitleApiService:
    """Service gọi đến endpoint dịch phụ đề phân tán /api/subtitle"""
    
    def __init__(self, config: ServerConfig, auth_service: AuthService):
        self.config = config
        self.auth = auth_service

    def _create_session_id(self) -> str:
        """Tạo Session ID duy nhất theo format gợi ý trong API doc"""
        return f"job-{datetime.now().strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:6]}"

    def start_translation_job(self, srt_lines: List[Dict], prompt: str, system_instruction: str) -> Tuple[bool, str, Optional[str]]:
        """
        Bắt đầu một job dịch mới.
        Returns: (success, message, session_id)
        """
        session_id = self._create_session_id()
        url = f"{self.config.base_url}/api/subtitle/translate"
        
        payload = {
            "sessionId": session_id,
            "prompt": prompt,
            "systemInstruction": system_instruction,
            "lines": srt_lines,
            "model": "gemini-2.5-flash",
            "callbackUrl": None
        }
        
        logger.info(f"Gửi job dịch mới với Session ID: {session_id}")
        logger.debug(f"Payload (first 2 lines): {json.dumps({**payload, 'lines': payload['lines'][:2]}, indent=2)}")

        try:
            response = requests.post(url, json=payload, headers=self.auth.get_auth_headers(), timeout=self.config.timeout)
            
            if response.status_code == 200:
                data = response.json()
                return True, data.get('message', 'Job đã được tạo.'), session_id
            
            error_message = f"Lỗi {response.status_code}: {response.text}"
            logger.error(error_message)
            return False, error_message, None
            
        except requests.RequestException as e:
            logger.error(f"Lỗi kết nối khi bắt đầu job: {e}")
            return False, f"Lỗi kết nối: {e}", None

    def poll_status(self, session_id: str) -> Dict:
        """Lấy trạng thái job. Trả về một dictionary chứa thông tin trạng thái."""
        url = f"{self.config.base_url}/api/subtitle/status/{session_id}"
        try:
            response = requests.get(url, headers=self.auth.get_auth_headers(), timeout=self.config.timeout)
            if response.status_code == 200:
                return response.json()
            
            if response.status_code == 404:
                return {"status": "failed", "error": f"Không tìm thấy job với ID: {session_id}"}

            return {"status": "failed", "error": f"Lỗi HTTP {response.status_code}"}
        except requests.RequestException as e:
            logger.error(f"Lỗi kết nối khi polling status: {e}")
            return {"status": "failed", "error": f"Lỗi kết nối: {e}"}

    def get_results(self, session_id: str) -> Dict:
        """Lấy kết quả cuối cùng của job."""
        url = f"{self.config.base_url}/api/subtitle/results/{session_id}"
        try:
            response = requests.get(url, headers=self.auth.get_auth_headers(), timeout=self.config.timeout)
            if response.status_code == 200:
                return response.json()
            return {"status": "failed", "error": f"Lỗi HTTP {response.status_code}"}
        except requests.RequestException as e:
            logger.error(f"Lỗi kết nối khi lấy kết quả: {e}")
            return {"status": "failed", "error": f"Lỗi kết nối: {e}"}

# ==============================================================================
# GUI APPLICATION (Đã chỉnh sửa)
# ==============================================================================

class TranslationApp:
    """Ứng dụng GUI dịch SRT"""
    
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("QuickTranslate Client")
        self.root.geometry("900x750")
        self.root.minsize(800, 650)
        
        self.config = ServerConfig()
        self.auth = AuthService(self.config)
        self.translator: Optional[SubtitleApiService] = None
        
        self.is_translating = False
        self.stop_requested = False
        self.log_queue = queue.Queue()
        
        self._create_ui()
        self._update_log()
    
    def _create_ui(self):
        """Tạo giao diện người dùng"""
        main_frame = ttk.Frame(self.root, padding="10")
        main_frame.grid(row=0, column=0, sticky="nsew")
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        main_frame.columnconfigure(0, weight=1)
        
        # === Server & Auth ===
        server_frame = ttk.LabelFrame(main_frame, text="1. Cấu hình & Đăng nhập", padding="5")
        server_frame.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        server_frame.columnconfigure(1, weight=1)
        
        ttk.Label(server_frame, text="Server URL:").grid(row=0, column=0, sticky="w", padx=5)
        self.server_url_entry = ttk.Entry(server_frame, width=60)
        self.server_url_entry.grid(row=0, column=1, sticky="ew", padx=5)
        self.server_url_entry.insert(0, "http://localhost:5000")
        
        auth_subframe = ttk.Frame(server_frame)
        auth_subframe.grid(row=1, column=0, columnspan=2, sticky='ew', pady=5)
        ttk.Label(auth_subframe, text="Username:").pack(side="left", padx=(5,2))
        self.username_entry = ttk.Entry(auth_subframe, width=20)
        self.username_entry.pack(side="left", padx=(0,10))
        
        ttk.Label(auth_subframe, text="Password:").pack(side="left", padx=(5,2))
        self.password_entry = ttk.Entry(auth_subframe, width=20, show="*")
        self.password_entry.pack(side="left", padx=(0,10))
        
        ttk.Button(auth_subframe, text="Đăng nhập", command=self._login).pack(side="left", padx=5)
        ttk.Button(auth_subframe, text="Đăng ký", command=self._register_popup).pack(side="left", padx=5)
        
        self.auth_status_label = ttk.Label(server_frame, text="Chưa đăng nhập", foreground="red")
        self.auth_status_label.grid(row=2, column=0, columnspan=2, pady=5)
        
        # === Translation Settings ===
        settings_frame = ttk.LabelFrame(main_frame, text="2. Cài đặt dịch", padding="5")
        settings_frame.grid(row=1, column=0, sticky="ew", pady=(0, 10))
        settings_frame.columnconfigure(1, weight=1)
        
        ttk.Label(settings_frame, text="Prompt:").grid(row=0, column=0, sticky="nw", padx=5)
        self.prompt_text = scrolledtext.ScrolledText(settings_frame, width=70, height=4)
        self.prompt_text.grid(row=0, column=1, sticky="ew", padx=5, pady=5)
        self.prompt_text.insert("1.0", "Dịch phụ đề sau sang tiếng Việt.\nGiữ nguyên format: index|text đã dịch\nChỉ trả về kết quả dịch, không giải thích.")
        
        ttk.Label(settings_frame, text="System Instruction:").grid(row=1, column=0, sticky="nw", padx=5)
        self.instruction_text = scrolledtext.ScrolledText(settings_frame, width=70, height=4)
        self.instruction_text.grid(row=1, column=1, sticky="ew", padx=5, pady=5)
        self.instruction_text.insert("1.0", "Bạn là dịch giả phụ đề phim chuyên nghiệp.\n- Dịch tự nhiên, phù hợp ngữ cảnh\n- Giữ nguyên tên riêng phổ biến\n- Không thêm bớt ý nghĩa")
        
        # === File Selection ===
        file_frame = ttk.LabelFrame(main_frame, text="3. Chọn file", padding="5")
        file_frame.grid(row=2, column=0, sticky="ew", pady=(0, 10))
        file_frame.columnconfigure(1, weight=1)
        
        ttk.Label(file_frame, text="File SRT cần dịch:").grid(row=0, column=0, sticky="w", padx=5)
        self.file_entry = ttk.Entry(file_frame, width=60)
        self.file_entry.grid(row=0, column=1, sticky="ew", padx=5)
        ttk.Button(file_frame, text="Chọn File...", command=self._select_srt_file).grid(row=0, column=2, padx=5)
        
        # === Action & Progress ===
        action_frame = ttk.LabelFrame(main_frame, text="4. Thực thi", padding="10")
        action_frame.grid(row=3, column=0, sticky="ew", pady=(0, 10))
        action_frame.columnconfigure(1, weight=1)

        self.start_btn = ttk.Button(action_frame, text="🚀 Bắt đầu dịch", command=self._start_translation, style="Accent.TButton")
        self.start_btn.grid(row=0, column=0, padx=5, pady=5)
        
        self.stop_btn = ttk.Button(action_frame, text="⏹ Dừng", command=self._stop_translation, state="disabled")
        self.stop_btn.grid(row=1, column=0, padx=5, pady=5)

        self.progress_bar = ttk.Progressbar(action_frame, variable=tk.DoubleVar(value=0), maximum=100)
        self.progress_bar.grid(row=0, column=1, sticky="ew", padx=10, pady=5)
        
        self.progress_label = ttk.Label(action_frame, text="Sẵn sàng")
        self.progress_label.grid(row=1, column=1, sticky="ew", padx=10, pady=5)
        
        # === Log ===
        log_frame = ttk.LabelFrame(main_frame, text="Log", padding="5")
        log_frame.grid(row=4, column=0, sticky="nsew", pady=(0, 10))
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)
        main_frame.rowconfigure(4, weight=1)
        
        self.log_text = scrolledtext.ScrolledText(log_frame, height=10, state="disabled")
        self.log_text.grid(row=0, column=0, sticky="nsew")
        ttk.Button(log_frame, text="Xóa Log", command=self._clear_log).grid(row=1, column=0, sticky="e", pady=5)

        style = ttk.Style()
        style.configure("Accent.TButton", font=("Segoe UI", 10, "bold"), padding=10)

    def _update_server_url(self):
        url = self.server_url_entry.get().strip()
        if url:
            self.config.base_url = url.rstrip('/')
            self.auth = AuthService(self.config)
            self.translator = None if not self.auth.is_authenticated() else SubtitleApiService(self.config, self.auth)
            self._log(f"Server URL đã cập nhật: {self.config.base_url}")
            
    def _login(self):
        username = self.username_entry.get().strip()
        password = self.password_entry.get()
        if not username or not password:
            messagebox.showerror("Lỗi", "Vui lòng nhập username và password")
            return
        
        self._update_server_url()
        success, message = self.auth.login(username, password)
        
        if success:
            self.translator = SubtitleApiService(self.config, self.auth)
            self.auth_status_label.config(text=f"Đã đăng nhập: {self.auth.session.username}", foreground="green")
            self._log(message)
        else:
            messagebox.showerror("Lỗi đăng nhập", message)
            self._log(f"Lỗi đăng nhập: {message}")
    
    def _register_popup(self):
        reg_win = tk.Toplevel(self.root)
        reg_win.title("Đăng ký tài khoản mới")
        reg_win.geometry("350x200")
        reg_win.transient(self.root)
        reg_win.grab_set()

        frame = ttk.Frame(reg_win, padding=10)
        frame.pack(expand=True, fill="both")
        
        ttk.Label(frame, text="Username:").grid(row=0, column=0, sticky='w', pady=2)
        reg_user = ttk.Entry(frame)
        reg_user.grid(row=0, column=1, sticky='ew', pady=2)

        ttk.Label(frame, text="Password:").grid(row=1, column=0, sticky='w', pady=2)
        reg_pass = ttk.Entry(frame, show="*")
        reg_pass.grid(row=1, column=1, sticky='ew', pady=2)

        ttk.Label(frame, text="Email:").grid(row=2, column=0, sticky='w', pady=2)
        reg_email = ttk.Entry(frame)
        reg_email.grid(row=2, column=1, sticky='ew', pady=2)

        def do_register():
            u, p, e = reg_user.get().strip(), reg_pass.get(), reg_email.get().strip()
            if not all([u, p, e]):
                messagebox.showerror("Lỗi", "Vui lòng nhập đủ thông tin.", parent=reg_win)
                return
            
            self._update_server_url()
            success, message = self.auth.register(u, p, e)
            if success:
                messagebox.showinfo("Thành công", message, parent=reg_win)
                self._log(f"Đăng ký thành công cho user '{u}'.")
                reg_win.destroy()
            else:
                messagebox.showerror("Lỗi", message, parent=reg_win)
                self._log(f"Lỗi đăng ký: {message}")

        ttk.Button(frame, text="Đăng ký", command=do_register).grid(row=3, columnspan=2, pady=10)

    def _select_srt_file(self):
        filepath = filedialog.askopenfilename(
            title="Chọn file .SRT",
            filetypes=[("SRT files", "*.srt"), ("All files", "*.*")]
        )
        if filepath:
            self.file_entry.delete(0, tk.END)
            self.file_entry.insert(0, filepath)
            self._log(f"Đã chọn file: {os.path.basename(filepath)}")
    
    def _log(self, message: str):
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_queue.put(f"[{timestamp}] {message}")
    
    def _update_log(self):
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
        self.log_text.config(state="normal")
        self.log_text.delete("1.0", tk.END)
        self.log_text.config(state="disabled")

    def _start_translation(self):
        if not self.auth.is_authenticated() or self.translator is None:
            messagebox.showerror("Lỗi", "Vui lòng đăng nhập trước")
            return
        
        filepath = self.file_entry.get().strip()
        if not filepath or not os.path.isfile(filepath):
            messagebox.showerror("Lỗi", "Vui lòng chọn một file SRT hợp lệ")
            return
        
        prompt = self.prompt_text.get("1.0", tk.END).strip()
        instruction = self.instruction_text.get("1.0", tk.END).strip()
        
        if not prompt or not instruction:
            messagebox.showerror("Lỗi", "Vui lòng nhập Prompt và System Instruction")
            return
        
        self.is_translating = True
        self.stop_requested = False
        self.start_btn.config(state="disabled")
        self.stop_btn.config(state="normal")
        self.progress_bar['value'] = 0
        
        thread = threading.Thread(
            target=self._translation_worker,
            args=(filepath, prompt, instruction),
            daemon=True,
            name="TranslationWorker"
        )
        thread.start()
    
    def _translation_worker(self, filepath: str, prompt: str, instruction: str):
        try:
            # 1. Đọc và phân tích file SRT
            self._log(f"Đang đọc file: {os.path.basename(filepath)}")
            with open(filepath, 'r', encoding='utf-8-sig') as f:
                srt_content = f.read()
            
            original_entries = parse_srt(srt_content)
            if not original_entries:
                raise ValueError("File SRT rỗng hoặc không hợp lệ.")

            # Chuyển đổi sang định dạng mà API yêu cầu
            srt_lines_for_api = [{"index": entry.index, "text": entry.text} for entry in original_entries]
            self._log(f"Đã phân tích được {len(srt_lines_for_api)} dòng phụ đề.")

            # 2. Bắt đầu job dịch
            self._log("Đang gửi yêu cầu dịch đến server...")
            success, message, session_id = self.translator.start_translation_job(srt_lines_for_api, prompt, instruction)

            if not success:
                raise Exception(f"Không thể bắt đầu job: {message}")
            
            self._log(f"Server đã chấp nhận job. Session ID: {session_id}")

            # 3. Polling để lấy trạng thái
            while not self.stop_requested:
                status_data = self.translator.poll_status(session_id)
                status = status_data.get('status', 'unknown').lower()
                
                self._log(f"Trạng thái job: {status}, Tiến trình: {status_data.get('progress', 0):.1f}%")

                if status == 'completed':
                    self.root.after(0, lambda: self.progress_bar.config(value=100))
                    self.root.after(0, lambda: self.progress_label.config(text="Hoàn thành! Đang lấy kết quả..."))
                    self._log("Job hoàn thành. Đang tải kết quả...")
                    break

                if status in ['failed', 'partialcompleted']:
                    error_msg = status_data.get('error', 'Lỗi không xác định từ server.')
                    raise Exception(f"Job thất bại với trạng thái '{status}'. Lỗi: {error_msg}")

                progress = status_data.get('progress', self.progress_bar['value'])
                completed_lines = status_data.get('completedLines', 0)
                total_lines = status_data.get('totalLines', len(original_entries))

                self.root.after(0, lambda p=progress: self.progress_bar.config(value=p))
                
                status_text = f"Trạng thái: {status.capitalize()} | {completed_lines}/{total_lines} dòng ({progress:.1f}%)"
                self.root.after(0, lambda: self.progress_label.config(text=status_text))
                
                time.sleep(3 if total_lines < 500 else 5)

            if self.stop_requested:
                self._log("Người dùng đã yêu cầu dừng.")
                return

            # 4. Lấy kết quả cuối cùng
            self._log("Đang lấy kết quả dịch...")
            result_data = self.translator.get_results(session_id)
            if result_data.get('status', '').lower() != 'completed':
                raise Exception(f"Lấy kết quả thất bại: {result_data.get('error', 'Không có dữ liệu trả về')}")

            translated_items = result_data.get('results', [])
            if not translated_items:
                 raise Exception("Kết quả trả về không có nội dung dịch.")

            translations_map = {item['index']: item['translated'] for item in translated_items}
            
            # 5. Xây dựng lại file SRT và lưu
            self._log("Đang tạo file SRT đã dịch...")
            final_srt_content = build_srt(original_entries, translations_map)
            
            base_dir = os.path.dirname(filepath)
            filename = os.path.basename(filepath)
            name, ext = os.path.splitext(filename)
            
            output_folder = os.path.join(base_dir, "Đã dịch")
            os.makedirs(output_folder, exist_ok=True)
            output_path = os.path.join(output_folder, f"{name}_vi{ext}")

            with open(output_path, 'w', encoding='utf-8') as f:
                f.write(final_srt_content)
            
            self._log("🎉 Dịch thành công!")
            self._log(f"File kết quả đã được lưu tại: {output_path}")
            self.root.after(0, lambda: messagebox.showinfo("Hoàn thành", f"Đã dịch xong!\nFile đã được lưu tại:\n{output_path}"))

        except Exception as e:
            logger.error(f"Lỗi trong quá trình dịch: {e}", exc_info=True)
            self._log(f"❌ LỖI: {e}")
            self.root.after(0, lambda: messagebox.showerror("Lỗi", str(e)))
        finally:
            self.root.after(0, self._translation_completed)
            
    def _stop_translation(self):
        """Dừng quá trình dịch"""
        if self.is_translating:
            self.stop_requested = True
            self._log("...Đang yêu cầu dừng...")
            self.stop_btn.config(state="disabled")
    
    def _translation_completed(self):
        """Được gọi khi dịch xong hoặc bị lỗi/dừng"""
        self.is_translating = False
        self.start_btn.config(state="normal")
        self.stop_btn.config(state="disabled")
        self.progress_label.config(text="Sẵn sàng")


def main():
    """Entry point"""
    try:
        root = tk.Tk()
        app = TranslationApp(root)
        root.mainloop()
    except Exception as e:
        logging.critical(f"Unhandled exception in main loop: {e}", exc_info=True)
        messagebox.showerror("Lỗi nghiêm trọng", f"Ứng dụng gặp lỗi không xác định:\n{e}")

if __name__ == "__main__":
    main()