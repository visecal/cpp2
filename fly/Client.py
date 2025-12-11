"""
Subtitle Translator Client
UI Application để test server dịch phụ đề
"""

import tkinter as tk
from tkinter import ttk, filedialog, messagebox, scrolledtext
import threading
import requests
import time
import uuid
import re
import json
from datetime import datetime
from pathlib import Path
from dataclasses import dataclass
from typing import Optional

# ============== SRT PARSER ==============

@dataclass
class SRTEntry:
    index: int
    start_time: str
    end_time: str
    text: str

def parse_srt(content: str) -> list[SRTEntry]:
    """Parse file SRT thành list entries"""
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
                    start_time = time_match.group(1)
                    end_time = time_match.group(2)
                    text = '\n'.join(lines[2:]).strip()
                    entries.append(SRTEntry(index, start_time, end_time, text))
            except ValueError:
                continue
    return entries

def build_srt(entries: list[SRTEntry], translations: dict[int, str]) -> str:
    """Xây dựng nội dung SRT từ entries và translations"""
    lines = []
    for entry in entries:
        lines.append(str(entry.index))
        lines.append(f"{entry.start_time} --> {entry.end_time}")
        translated = translations.get(entry.index, entry.text)
        lines.append(translated)
        lines.append("")
    return '\n'.join(lines)

# ============== MAIN APPLICATION ==============

class SubtitleTranslatorApp:
    def __init__(self, root):
        self.root = root
        self.root.title("🎬 Subtitle Translator Client")
        self.root.geometry("1100x900")
        self.root.minsize(1000, 800)
        
        # State
        self.srt_entries: list[SRTEntry] = []
        self.current_file: Optional[str] = None
        self.is_translating = False
        self.translations: dict[int, str] = {}
        
        # Style
        style = ttk.Style()
        style.configure("Title.TLabel", font=("Helvetica", 12, "bold"))
        
        self.setup_ui()
        self.load_settings()
    
    def setup_ui(self):
        """Thiết lập giao diện"""
        main_frame = ttk.Frame(self.root, padding="10")
        main_frame.pack(fill=tk.BOTH, expand=True)
        
        # ===== SERVER CONFIG =====
        server_frame = ttk.LabelFrame(main_frame, text="🌐 Server Configuration", padding="10")
        server_frame.pack(fill=tk.X, pady=(0, 10))
        
        server_row = ttk.Frame(server_frame)
        server_row.pack(fill=tk.X)
        
        ttk.Label(server_row, text="Server URL:").pack(side=tk.LEFT)
        self.server_url = ttk.Entry(server_row, width=50)
        self.server_url.insert(0, "http://localhost:8080")
        self.server_url.pack(side=tk.LEFT, padx=(5, 10))
        
        ttk.Button(server_row, text="🔌 Test", command=self.test_connection).pack(side=tk.LEFT, padx=(0, 10))
        ttk.Button(server_row, text="⚙️ Config", command=self.show_server_config).pack(side=tk.LEFT)
        
        # ===== API KEYS =====
        api_frame = ttk.LabelFrame(main_frame, text="🔑 API Keys (one per line)", padding="10")
        api_frame.pack(fill=tk.X, pady=(0, 10))
        
        self.api_keys_text = scrolledtext.ScrolledText(api_frame, height=3, width=80)
        self.api_keys_text.pack(fill=tk.X)
        
        # ===== MODEL & PARAMS =====
        params_frame = ttk.LabelFrame(main_frame, text="⚡ Translation Parameters", padding="10")
        params_frame.pack(fill=tk.X, pady=(0, 10))
        
        # Row 1
        row1 = ttk.Frame(params_frame)
        row1.pack(fill=tk.X, pady=(0, 5))
        
        ttk.Label(row1, text="Model:").pack(side=tk.LEFT)
        self.model_var = tk.StringVar(value="gemini-2.5-flash")
        model_combo = ttk.Combobox(row1, textvariable=self.model_var, width=25, values=[
            "gemini-2.5-flash",
            "gemini-2.5-pro",
            "gemini-2.0-flash",
            "gemini-2.0-flash-lite",
            "gemini-1.5-flash",
            "gemini-1.5-pro",
        ])
        model_combo.pack(side=tk.LEFT, padx=(5, 20))
        
        ttk.Label(row1, text="Batch Size:").pack(side=tk.LEFT)
        self.batch_size = ttk.Spinbox(row1, from_=5, to=100, width=8)
        self.batch_size.set(30)
        self.batch_size.pack(side=tk.LEFT, padx=(5, 20))
        
        ttk.Label(row1, text="Poll Interval (s):").pack(side=tk.LEFT)
        self.poll_interval = ttk.Spinbox(row1, from_=1, to=60, width=8)
        self.poll_interval.set(3)
        self.poll_interval.pack(side=tk.LEFT, padx=(5, 0))
        
        # Row 2 - Thinking
        row2 = ttk.Frame(params_frame)
        row2.pack(fill=tk.X, pady=(5, 0))
        
        self.thinking_enabled = tk.BooleanVar(value=False)
        ttk.Checkbutton(row2, text="🧠 Enable Thinking", variable=self.thinking_enabled,
                       command=self.toggle_thinking).pack(side=tk.LEFT)
        
        ttk.Label(row2, text="Budget:").pack(side=tk.LEFT, padx=(20, 5))
        self.thinking_budget = ttk.Spinbox(row2, from_=0, to=24576, width=10, state=tk.DISABLED)
        self.thinking_budget.set(8192)
        self.thinking_budget.pack(side=tk.LEFT)
        
        # Thinking presets
        ttk.Label(row2, text="Presets:").pack(side=tk.LEFT, padx=(20, 5))
        ttk.Button(row2, text="Light (4K)", width=10,
                  command=lambda: self.set_thinking(4096)).pack(side=tk.LEFT, padx=2)
        ttk.Button(row2, text="Medium (8K)", width=10,
                  command=lambda: self.set_thinking(8192)).pack(side=tk.LEFT, padx=2)
        ttk.Button(row2, text="Heavy (16K)", width=10,
                  command=lambda: self.set_thinking(16384)).pack(side=tk.LEFT, padx=2)
        ttk.Button(row2, text="Max (24K)", width=10,
                  command=lambda: self.set_thinking(24576)).pack(side=tk.LEFT, padx=2)
        
        # ===== PROMPT SECTION =====
        prompt_frame = ttk.LabelFrame(main_frame, text="📝 Prompt & System Instruction", padding="10")
        prompt_frame.pack(fill=tk.X, pady=(0, 10))
        
        # Prompt
        prompt_row = ttk.Frame(prompt_frame)
        prompt_row.pack(fill=tk.X)
        ttk.Label(prompt_row, text="Prompt:", style="Title.TLabel").pack(side=tk.LEFT)
        
        self.prompt_text = scrolledtext.ScrolledText(prompt_frame, height=3, width=80)
        self.prompt_text.insert(tk.END, """Dịch phụ đề sau sang tiếng Việt.
Giữ nguyên format: index|text đã dịch
Chỉ trả về kết quả dịch, không giải thích.""")
        self.prompt_text.pack(fill=tk.X, pady=(5, 10))
        
        # System Instruction
        ttk.Label(prompt_frame, text="System Instruction:", style="Title.TLabel").pack(anchor=tk.W)
        self.system_instruction = scrolledtext.ScrolledText(prompt_frame, height=3, width=80)
        self.system_instruction.insert(tk.END, """Bạn là dịch giả phụ đề phim chuyên nghiệp.
- Dịch tự nhiên, phù hợp ngữ cảnh
- Giữ nguyên tên riêng phổ biến
- Không thêm bớt ý nghĩa""")
        self.system_instruction.pack(fill=tk.X, pady=(5, 0))
        
        # Preset buttons
        preset_frame = ttk.Frame(prompt_frame)
        preset_frame.pack(fill=tk.X, pady=(10, 0))
        ttk.Label(preset_frame, text="Quick Presets:").pack(side=tk.LEFT, padx=(0, 10))
        ttk.Button(preset_frame, text="🎬 Phim thường", 
                  command=lambda: self.apply_preset("normal")).pack(side=tk.LEFT, padx=2)
        ttk.Button(preset_frame, text="⚔️ Tu tiên/Cổ trang", 
                  command=lambda: self.apply_preset("xianxia")).pack(side=tk.LEFT, padx=2)
        ttk.Button(preset_frame, text="🎌 Anime", 
                  command=lambda: self.apply_preset("anime")).pack(side=tk.LEFT, padx=2)
        ttk.Button(preset_frame, text="🎭 Hàn Quốc", 
                  command=lambda: self.apply_preset("kdrama")).pack(side=tk.LEFT, padx=2)
        ttk.Button(preset_frame, text="📚 Documentary", 
                  command=lambda: self.apply_preset("documentary")).pack(side=tk.LEFT, padx=2)
        
        # ===== FILE SECTION =====
        file_frame = ttk.LabelFrame(main_frame, text="📁 SRT File", padding="10")
        file_frame.pack(fill=tk.BOTH, expand=True, pady=(0, 10))
        
        # File controls
        file_controls = ttk.Frame(file_frame)
        file_controls.pack(fill=tk.X, pady=(0, 10))
        
        ttk.Button(file_controls, text="📂 Open SRT", command=self.open_file).pack(side=tk.LEFT, padx=(0, 5))
        ttk.Button(file_controls, text="💾 Save Translation", command=self.save_translation).pack(side=tk.LEFT, padx=(0, 5))
        ttk.Button(file_controls, text="📋 Export JSON", command=self.export_json).pack(side=tk.LEFT, padx=(0, 5))
        
        self.file_label = ttk.Label(file_controls, text="No file loaded", foreground="gray")
        self.file_label.pack(side=tk.LEFT, padx=(20, 0))
        
        # Preview tabs
        preview_notebook = ttk.Notebook(file_frame)
        preview_notebook.pack(fill=tk.BOTH, expand=True)
        
        # Original tab
        original_frame = ttk.Frame(preview_notebook)
        preview_notebook.add(original_frame, text="📄 Original")
        self.original_preview = scrolledtext.ScrolledText(original_frame, height=8, state=tk.DISABLED)
        self.original_preview.pack(fill=tk.BOTH, expand=True)
        
        # Translation tab
        translation_frame = ttk.Frame(preview_notebook)
        preview_notebook.add(translation_frame, text="🌐 Translation")
        self.translation_preview = scrolledtext.ScrolledText(translation_frame, height=8, state=tk.DISABLED)
        self.translation_preview.pack(fill=tk.BOTH, expand=True)
        
        # Side by side tab
        sidebyside_frame = ttk.Frame(preview_notebook)
        preview_notebook.add(sidebyside_frame, text="↔️ Side by Side")
        self.sidebyside_preview = scrolledtext.ScrolledText(sidebyside_frame, height=8, state=tk.DISABLED)
        self.sidebyside_preview.pack(fill=tk.BOTH, expand=True)
        
        # ===== CONTROL SECTION =====
        control_frame = ttk.Frame(main_frame)
        control_frame.pack(fill=tk.X, pady=(0, 10))
        
        # Session ID
        ttk.Label(control_frame, text="Session ID:").pack(side=tk.LEFT)
        self.session_id = ttk.Entry(control_frame, width=35)
        self.session_id.pack(side=tk.LEFT, padx=(5, 5))
        ttk.Button(control_frame, text="🔄", width=3, command=self.generate_session_id).pack(side=tk.LEFT, padx=(0, 20))
        
        # Action buttons
        self.translate_btn = ttk.Button(control_frame, text="🚀 Start Translation", command=self.start_translation)
        self.translate_btn.pack(side=tk.LEFT, padx=(0, 5))
        
        self.cancel_btn = ttk.Button(control_frame, text="⏹ Cancel", command=self.cancel_translation, state=tk.DISABLED)
        self.cancel_btn.pack(side=tk.LEFT, padx=(0, 5))
        
        ttk.Button(control_frame, text="🔍 Check Status", command=self.check_status).pack(side=tk.LEFT)
        
        # ===== PROGRESS =====
        progress_frame = ttk.Frame(main_frame)
        progress_frame.pack(fill=tk.X, pady=(0, 10))
        
        self.progress_var = tk.DoubleVar(value=0)
        self.progress_bar = ttk.Progressbar(progress_frame, variable=self.progress_var, maximum=100, length=400)
        self.progress_bar.pack(fill=tk.X, side=tk.LEFT, expand=True, padx=(0, 10))
        
        self.status_label = ttk.Label(progress_frame, text="Ready", width=35)
        self.status_label.pack(side=tk.LEFT)
        
        # ===== LOG =====
        log_frame = ttk.LabelFrame(main_frame, text="📋 Log", padding="5")
        log_frame.pack(fill=tk.X)
        
        log_controls = ttk.Frame(log_frame)
        log_controls.pack(fill=tk.X)
        ttk.Button(log_controls, text="Clear", command=self.clear_log).pack(side=tk.RIGHT)
        
        self.log_text = scrolledtext.ScrolledText(log_frame, height=4)
        self.log_text.pack(fill=tk.X)
        
        # Generate initial session ID
        self.generate_session_id()
    
    def toggle_thinking(self):
        if self.thinking_enabled.get():
            self.thinking_budget.config(state=tk.NORMAL)
        else:
            self.thinking_budget.config(state=tk.DISABLED)
    
    def set_thinking(self, budget: int):
        self.thinking_enabled.set(True)
        self.thinking_budget.config(state=tk.NORMAL)
        self.thinking_budget.delete(0, tk.END)
        self.thinking_budget.insert(0, str(budget))
    
    def apply_preset(self, preset_type: str):
        presets = {
            "normal": {
                "prompt": """Dịch phụ đề sau sang tiếng Việt.
Giữ nguyên format: index|text đã dịch
Chỉ trả về kết quả dịch, không giải thích.""",
                "system": """Bạn là dịch giả phụ đề phim chuyên nghiệp.
- Dịch tự nhiên, phù hợp ngữ cảnh Việt Nam
- Giữ nguyên tên riêng phổ biến quốc tế
- Không thêm bớt ý nghĩa
- Sử dụng ngôn ngữ đời thường, dễ hiểu"""
            },
            "xianxia": {
                "prompt": """Dịch phụ đề phim tu tiên/cổ trang Trung Quốc sang tiếng Việt.
Giữ nguyên format: index|text đã dịch
Chỉ trả về kết quả dịch.""",
                "system": """Bạn là dịch giả phim Hoa ngữ cổ trang chuyên nghiệp.
- Thể loại: tu tiên, huyền huyễn, cổ trang, tiên hiệp
- Xưng hô cổ trang: ngươi, ta, hắn, nàng, tại hạ, các hạ, bổn tọa, sư huynh, sư đệ, sư muội
- Tên riêng phiên âm Hán Việt (Lý Tịnh, Thái Ất, Na Tra, Nguyên Thủy Thiên Tôn...)
- Thuật ngữ tu tiên: linh lực, ma hoàn, thiên kiếp, kim đan, nguyên anh, trúc cơ, kết đan...
- Môn phái: tông môn, đại phái, tiên môn...
- Dịch tự nhiên nhưng giữ không khí cổ trang trang trọng"""
            },
            "anime": {
                "prompt": """Dịch phụ đề anime Nhật Bản sang tiếng Việt.
Giữ nguyên format: index|text đã dịch
Chỉ trả về kết quả dịch.""",
                "system": """Bạn là dịch giả anime chuyên nghiệp.
- Giữ nguyên tên nhân vật tiếng Nhật (Naruto, Luffy, Goku...)
- Giữ các hậu tố kính ngữ: -san, -kun, -chan, -sama, -sensei, -senpai
- Dịch tự nhiên, phù hợp giọng điệu và tính cách nhân vật
- Từ đặc trưng có thể giữ: senpai, kouhai, baka, kawaii, sugoi...
- Câu cảm thán giữ nguyên phong cách anime
- Chiêu thức/kỹ năng có thể giữ tên gốc hoặc dịch tùy ngữ cảnh"""
            },
            "kdrama": {
                "prompt": """Dịch phụ đề phim Hàn Quốc sang tiếng Việt.
Giữ nguyên format: index|text đã dịch
Chỉ trả về kết quả dịch.""",
                "system": """Bạn là dịch giả phim Hàn Quốc chuyên nghiệp.
- Giữ nguyên tên nhân vật Hàn Quốc
- Xưng hô: oppa, unnie, hyung, noona có thể giữ hoặc dịch tùy ngữ cảnh
- Kính ngữ: anh/chị/em phù hợp với mối quan hệ
- Từ đặc trưng: aegyo, daebak, fighting/hwaiting... có thể giữ
- Dịch tự nhiên, giữ cảm xúc và giọng điệu nhân vật
- Lưu ý văn hóa công sở và gia đình Hàn Quốc"""
            },
            "documentary": {
                "prompt": """Dịch phụ đề phim tài liệu sang tiếng Việt.
Giữ nguyên format: index|text đã dịch
Chỉ trả về kết quả dịch.""",
                "system": """Bạn là dịch giả phim tài liệu chuyên nghiệp.
- Dịch chính xác, khách quan
- Thuật ngữ chuyên ngành cần chính xác
- Tên riêng, địa danh giữ nguyên hoặc phiên âm phổ biến
- Số liệu, thống kê giữ nguyên
- Giọng văn trang trọng, học thuật
- Giải thích thuật ngữ khó nếu cần thiết"""
            }
        }
        
        if preset_type in presets:
            preset = presets[preset_type]
            self.prompt_text.delete("1.0", tk.END)
            self.prompt_text.insert(tk.END, preset["prompt"])
            self.system_instruction.delete("1.0", tk.END)
            self.system_instruction.insert(tk.END, preset["system"])
            self.log(f"Applied preset: {preset_type}")
    
    def generate_session_id(self):
        new_id = f"job-{datetime.now().strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:6]}"
        self.session_id.delete(0, tk.END)
        self.session_id.insert(0, new_id)
    
    def log(self, message: str):
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_text.insert(tk.END, f"[{timestamp}] {message}\n")
        self.log_text.see(tk.END)
    
    def clear_log(self):
        self.log_text.delete("1.0", tk.END)
    
    def test_connection(self):
        url = self.server_url.get().strip()
        try:
            response = requests.get(url, timeout=5)
            if response.status_code == 200:
                data = response.json()
                config = data.get('config', {})
                msg = f"✅ Connected!\n\nServer: {data.get('service', 'Unknown')}\n"
                msg += f"RPM: {config.get('rpm', 'N/A')}\n"
                msg += f"Max Retries: {config.get('maxRetries', 'N/A')}\n"
                msg += f"Active Jobs: {data.get('activeJobs', 0)}\n"
                msg += f"Total Jobs: {data.get('totalJobs', 0)}"
                messagebox.showinfo("Connection Test", msg)
                self.log(f"Connected to server: {url}")
            else:
                messagebox.showerror("Error", f"Server returned: {response.status_code}")
        except Exception as e:
            messagebox.showerror("Connection Failed", str(e))
            self.log(f"Connection failed: {e}")
    
    def show_server_config(self):
        """Show server config dialog"""
        url = self.server_url.get().strip()
        try:
            response = requests.get(f"{url}/config", timeout=5)
            if response.status_code == 200:
                config = response.json()
                
                # Create dialog
                dialog = tk.Toplevel(self.root)
                dialog.title("Server Configuration")
                dialog.geometry("300x200")
                dialog.transient(self.root)
                dialog.grab_set()
                
                frame = ttk.Frame(dialog, padding="20")
                frame.pack(fill=tk.BOTH, expand=True)
                
                # RPM
                ttk.Label(frame, text="RPM (Requests Per Minute):").pack(anchor=tk.W)
                rpm_var = tk.StringVar(value=str(config.get('rpm', 5)))
                rpm_entry = ttk.Entry(frame, textvariable=rpm_var, width=10)
                rpm_entry.pack(anchor=tk.W, pady=(0, 10))
                
                # Max Retries
                ttk.Label(frame, text="Max Retries:").pack(anchor=tk.W)
                retries_var = tk.StringVar(value=str(config.get('maxRetries', 5)))
                retries_entry = ttk.Entry(frame, textvariable=retries_var, width=10)
                retries_entry.pack(anchor=tk.W, pady=(0, 20))
                
                def save_config():
                    try:
                        payload = {
                            "rpm": int(rpm_var.get()),
                            "maxRetries": int(retries_var.get())
                        }
                        resp = requests.post(f"{url}/config", json=payload, timeout=5)
                        if resp.status_code == 200:
                            self.log("Server config updated")
                            messagebox.showinfo("Success", "Config updated!")
                            dialog.destroy()
                        else:
                            messagebox.showerror("Error", resp.text)
                    except Exception as e:
                        messagebox.showerror("Error", str(e))
                
                ttk.Button(frame, text="Save", command=save_config).pack(side=tk.LEFT, padx=(0, 10))
                ttk.Button(frame, text="Cancel", command=dialog.destroy).pack(side=tk.LEFT)
                
        except Exception as e:
            messagebox.showerror("Error", f"Failed to get config:\n{str(e)}")
    
    def open_file(self):
        file_path = filedialog.askopenfilename(
            title="Select SRT File",
            filetypes=[("SRT files", "*.srt"), ("All files", "*.*")]
        )
        
        if file_path:
            try:
                content = None
                for encoding in ['utf-8', 'utf-8-sig', 'utf-16', 'gbk', 'gb2312', 'big5', 'latin-1']:
                    try:
                        with open(file_path, 'r', encoding=encoding) as f:
                            content = f.read()
                        break
                    except UnicodeDecodeError:
                        continue
                
                if content is None:
                    raise ValueError("Could not decode file")
                
                self.srt_entries = parse_srt(content)
                self.current_file = file_path
                self.translations = {}
                
                filename = Path(file_path).name
                self.file_label.config(text=f"✅ {filename} ({len(self.srt_entries)} entries)", foreground="green")
                
                # Update preview
                self.update_original_preview()
                self.log(f"Loaded: {filename} ({len(self.srt_entries)} entries)")
                
            except Exception as e:
                messagebox.showerror("Error", f"Failed to load file:\n{str(e)}")
                self.log(f"Error: {e}")
    
    def update_original_preview(self):
        self.original_preview.config(state=tk.NORMAL)
        self.original_preview.delete("1.0", tk.END)
        for entry in self.srt_entries[:100]:
            self.original_preview.insert(tk.END, f"[{entry.index}] {entry.text}\n")
        if len(self.srt_entries) > 100:
            self.original_preview.insert(tk.END, f"\n... và {len(self.srt_entries) - 100} dòng khác")
        self.original_preview.config(state=tk.DISABLED)
    
    def update_translation_preview(self):
        self.translation_preview.config(state=tk.NORMAL)
        self.translation_preview.delete("1.0", tk.END)
        
        for entry in self.srt_entries[:100]:
            translated = self.translations.get(entry.index, "[Chưa dịch]")
            self.translation_preview.insert(tk.END, f"[{entry.index}] {translated}\n")
        
        if len(self.srt_entries) > 100:
            self.translation_preview.insert(tk.END, f"\n... và {len(self.srt_entries) - 100} dòng khác")
        self.translation_preview.config(state=tk.DISABLED)
        
        # Side by side
        self.sidebyside_preview.config(state=tk.NORMAL)
        self.sidebyside_preview.delete("1.0", tk.END)
        
        for entry in self.srt_entries[:50]:
            translated = self.translations.get(entry.index, "[Chưa dịch]")
            self.sidebyside_preview.insert(tk.END, f"[{entry.index}] Original: {entry.text}\n")
            self.sidebyside_preview.insert(tk.END, f"[{entry.index}] Dịch: {translated}\n")
            self.sidebyside_preview.insert(tk.END, "─" * 80 + "\n")
        
        self.sidebyside_preview.config(state=tk.DISABLED)
    
    def save_translation(self):
        if not self.srt_entries:
            messagebox.showwarning("Warning", "No file loaded")
            return
        
        if not self.translations:
            messagebox.showwarning("Warning", "No translations available")
            return
        
        if self.current_file:
            original_path = Path(self.current_file)
            suggested_name = f"{original_path.stem}_vi{original_path.suffix}"
        else:
            suggested_name = "translated.srt"
        
        file_path = filedialog.asksaveasfilename(
            title="Save Translation",
            defaultextension=".srt",
            initialfile=suggested_name,
            filetypes=[("SRT files", "*.srt")]
        )
        
        if file_path:
            try:
                content = build_srt(self.srt_entries, self.translations)
                with open(file_path, 'w', encoding='utf-8') as f:
                    f.write(content)
                self.log(f"Saved: {file_path}")
                messagebox.showinfo("Success", f"Saved to:\n{file_path}")
            except Exception as e:
                messagebox.showerror("Error", str(e))
    
    def export_json(self):
        """Export translation as JSON"""
        if not self.translations:
            messagebox.showwarning("Warning", "No translations to export")
            return
        
        file_path = filedialog.asksaveasfilename(
            title="Export JSON",
            defaultextension=".json",
            filetypes=[("JSON files", "*.json")]
        )
        
        if file_path:
            try:
                data = {
                    "source_file": self.current_file,
                    "total_lines": len(self.srt_entries),
                    "translated_lines": len(self.translations),
                    "translations": [
                        {
                            "index": entry.index,
                            "original": entry.text,
                            "translated": self.translations.get(entry.index, "")
                        }
                        for entry in self.srt_entries
                    ]
                }
                with open(file_path, 'w', encoding='utf-8') as f:
                    json.dump(data, f, ensure_ascii=False, indent=2)
                self.log(f"Exported JSON: {file_path}")
                messagebox.showinfo("Success", f"Exported to:\n{file_path}")
            except Exception as e:
                messagebox.showerror("Error", str(e))
    
    def get_api_keys(self) -> list[str]:
        text = self.api_keys_text.get("1.0", tk.END)
        return [k.strip() for k in text.split('\n') if k.strip()]
    
    def start_translation(self):
        if not self.srt_entries:
            messagebox.showwarning("Warning", "Please load an SRT file first")
            return
        
        api_keys = self.get_api_keys()
        if not api_keys:
            messagebox.showwarning("Warning", "Please enter at least one API key")
            return
        
        session_id = self.session_id.get().strip()
        if not session_id:
            self.generate_session_id()
            session_id = self.session_id.get()
        
        lines = [{"index": e.index, "text": e.text} for e in self.srt_entries]
        
        payload = {
            "model": self.model_var.get(),
            "prompt": self.prompt_text.get("1.0", tk.END).strip(),
            "lines": lines,
            "systemInstruction": self.system_instruction.get("1.0", tk.END).strip(),
            "sessionId": session_id,
            "apiKeys": api_keys,
            "batchSize": int(self.batch_size.get())
        }
        
        if self.thinking_enabled.get():
            payload["thinkingBudget"] = int(self.thinking_budget.get())
        
        self.is_translating = True
        self.translate_btn.config(state=tk.DISABLED)
        self.cancel_btn.config(state=tk.NORMAL)
        self.progress_var.set(0)
        self.status_label.config(text="Submitting...")
        
        thread = threading.Thread(target=self.translation_worker, args=(payload,))
        thread.daemon = True
        thread.start()
    
    def translation_worker(self, payload: dict):
        server_url = self.server_url.get().strip()
        session_id = payload["sessionId"]
        poll_interval = int(self.poll_interval.get())
        
        try:
            self.log(f"Submitting job: {session_id}")
            self.log(f"Model: {payload['model']}, Batch: {payload['batchSize']}, Lines: {len(payload['lines'])}")
            if "thinkingBudget" in payload:
                self.log(f"Thinking Budget: {payload['thinkingBudget']}")
            
            response = requests.post(f"{server_url}/translate", json=payload, timeout=30)
            
            if response.status_code != 200:
                raise Exception(f"Submit failed: {response.status_code} - {response.text}")
            
            self.log("Job submitted successfully!")
            
            while self.is_translating:
                time.sleep(poll_interval)
                
                if not self.is_translating:
                    break
                
                status_response = requests.get(f"{server_url}/status/{session_id}", timeout=10)
                if status_response.status_code != 200:
                    self.log(f"Status check failed: {status_response.status_code}")
                    continue
                
                status = status_response.json()
                progress = status.get("progress", 0)
                job_status = status.get("status", "unknown")
                completed = status.get("completedLines", 0)
                total = status.get("totalLines", 0)
                
                self.root.after(0, lambda p=progress, s=job_status, c=completed, t=total: 
                               self.update_progress(p, f"{s}: {c}/{t}"))
                
                if job_status == "completed":
                    results_response = requests.get(f"{server_url}/results/{session_id}", timeout=30)
                    if results_response.status_code == 200:
                        results = results_response.json()
                        self.root.after(0, lambda r=results: self.handle_results(r))
                    break
                
                elif job_status == "failed":
                    error = status.get("error", "Unknown error")
                    self.root.after(0, lambda e=error: self.handle_error(e))
                    break
        
        except Exception as e:
            self.root.after(0, lambda: self.handle_error(str(e)))
        
        finally:
            self.root.after(0, self.translation_finished)
    
    def update_progress(self, progress: float, status: str):
        self.progress_var.set(progress)
        self.status_label.config(text=status)
    
    def handle_results(self, results: dict):
        self.log("✅ Translation completed!")
        
        for item in results.get("results", []):
            idx = item.get("index")
            translated = item.get("translated", "")
            if idx is not None:
                self.translations[idx] = translated
        
        self.update_translation_preview()
        messagebox.showinfo("Success", f"Translation completed!\n{len(self.translations)} lines translated.")
    
    def handle_error(self, error: str):
        self.log(f"❌ Error: {error}")
        messagebox.showerror("Translation Failed", error)
    
    def translation_finished(self):
        self.is_translating = False
        self.translate_btn.config(state=tk.NORMAL)
        self.cancel_btn.config(state=tk.DISABLED)
        if self.progress_var.get() >= 100:
            self.status_label.config(text="✅ Completed")
        elif self.progress_var.get() == 0:
            self.status_label.config(text="Ready")
    
    def cancel_translation(self):
        self.is_translating = False
        self.log("⏹ Translation cancelled")
        self.status_label.config(text="Cancelled")
    
    def check_status(self):
        """Manually check job status"""
        session_id = self.session_id.get().strip()
        if not session_id:
            messagebox.showwarning("Warning", "Please enter a Session ID")
            return
        
        url = self.server_url.get().strip()
        try:
            response = requests.get(f"{url}/status/{session_id}", timeout=10)
            if response.status_code == 200:
                status = response.json()
                msg = f"Session: {status.get('sessionId')}\n"
                msg += f"Status: {status.get('status')}\n"
                msg += f"Progress: {status.get('progress', 0):.1f}%\n"
                msg += f"Completed: {status.get('completedLines', 0)}/{status.get('totalLines', 0)}"
                if status.get('error'):
                    msg += f"\nError: {status.get('error')}"
                messagebox.showinfo("Job Status", msg)
            elif response.status_code == 404:
                messagebox.showwarning("Not Found", f"Job '{session_id}' not found")
            else:
                messagebox.showerror("Error", response.text)
        except Exception as e:
            messagebox.showerror("Error", str(e))
    
    def load_settings(self):
        settings_file = Path.home() / ".subtitle_translator_settings.json"
        if settings_file.exists():
            try:
                with open(settings_file, 'r') as f:
                    settings = json.load(f)
                
                if "server_url" in settings:
                    self.server_url.delete(0, tk.END)
                    self.server_url.insert(0, settings["server_url"])
                
                if "api_keys" in settings:
                    self.api_keys_text.delete("1.0", tk.END)
                    self.api_keys_text.insert(tk.END, "\n".join(settings["api_keys"]))
                
                self.log("Settings loaded from file")
            except:
                pass
    
    def save_settings(self):
        settings_file = Path.home() / ".subtitle_translator_settings.json"
        settings = {
            "server_url": self.server_url.get(),
            "api_keys": self.get_api_keys()
        }
        try:
            with open(settings_file, 'w') as f:
                json.dump(settings, f, indent=2)
        except:
            pass
    
    def on_closing(self):
        self.save_settings()
        self.root.destroy()

# ============== MAIN ==============

def main():
    root = tk.Tk()
    app = SubtitleTranslatorApp(root)
    root.protocol("WM_DELETE_WINDOW", app.on_closing)
    root.mainloop()

if __name__ == "__main__":
    main()
