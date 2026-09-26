@echo off
python -m PyInstaller --noconfirm --clean --name TouchPadClone --noconsole --onefile touchpad_clone\main.py
echo dist\TouchPadClone.exe 생성됨
