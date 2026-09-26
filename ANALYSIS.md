# TouchMousePointer 분석 (C:\Program Files\TouchMousePointer, v3.0.1.2)

제작: Lovesummertrue Software. Windows 8.1/10/11 태블릿·컨버터블용 가상 트랙패드(온스크린 마우스).

## 1. 설치 구성 분석

| 파일 | 역할 (추정) |
|---|---|
| `TouchMousePointer.exe` (1.2MB, 2025-02-26) | 본체: 투명 오버레이 윈도우 + 트랙패드 엔진. 화면 전체 반투명 윈도우를 띄워 터치를 상대좌표 마우스 이동으로 변환 |
| `TouchMousePointerTray.exe` (240KB) | 작업표시줄 트레이 아이콘. 탭=패드 ON/OFF, 롱탭=설정 메뉴 |
| `TouchMousePointerUI.exe` (58KB, 2019) | 구버전 설정 UI残재 (현재는 WebView2 기반 설정으로 추정) |
| `TouchMousePointer3012.dll` (185KB) | 버전 3.0.1.2 코어 엔진 (제스처·프리셋 파서) |
| `TouchMousePointerRes.dll` (2.1MB) | 리소스 (이미지·언어·아이콘) |
| `TabletProWebView2.dll` (3.5MB) + `TabletProStoreChecker.exe` | 라이선스/스토어 체크, 설정 WebView 렌더링 |
| `VJoy64.dll` (2013) | 가상 조이스틱. `Preset WASD gaming.ini`의 게임 키 매핑용 |
| `TouchMouseSetup.exe` | 인스톨러/관리자권한 재실행 헬퍼 |
| `invisible.cur` | 투명 커서. 풀스크린 모드에서 시스템 커서를 숨길 때 사용 |
| `FontAwesome.otf` / `FontAwesomeProSolid.otf` + `.txt` | 타일 아이콘(`%uF053` 같은 표기) 렌더링용 |
| `Pad.ini` | 내장 레이아웃 정의 (floatpad, leftpad, rightpad, toppad, bottompad, fullscreen, fingerside, virtualctrls …) |
| `Preset *.ini` (50여종) | 앱별 프리셋: Photoshop CC, Blender, Krita, Excel, OneNote, Zbrush, Premiere, WASD gaming, Windows 11 등 |
| `Preset advanced settings*.touchmouseptr_setting` | 고급 설정 내보내기 파일 |

실행 중 프로세스: `TouchMousePointer.exe` + `TouchMousePointerTray.exe` 상시 상주 확인됨.

## 2. 핵심 원리

- **마우스 드라이버 미사용.** 화면 전체(또는 플로팅 영역)에 투명 최상위 윈도우를 설치하고 터치/펜 입력을 가로채 `SendInput` 계열로 표준 커서 조작으로 변환. 저사양 태블릿에서 무거울 수 있다는 공식 주의문과 일치.
- **상대좌표 트랙패드:** 손가락 이동 델타 × 속도/가속도 = 커서 이동. 손가락이 타깃을 가리지 않아 롤오버/툴팁/리사이즈가 가능해짐.
- **투명 커서:** 풀스크린 시 `invisible.cur`로 실제 커서를 숨기고 가짜 화살표/소나/궤적선을 오버레이로 그림 (시각화 3종).
- **타일 레이아웃 엔진:** 모든 패드는 INI 섹션+타일 집합. 예(`Preset Default.ini [floatpad]`):
  `tile000=padframe,0,20,100,80` = 틀, `tile001=lbtn,0,0,50,20` = 좌버튼 … 마지막 `tile004=pad,0,0,100,100` = 투명 입력층(항상 최상위). 좌표는 0~100 % 상대값, `scale`, `aspect`로 비율 조정.
- **타일 타입:** `pad`(입력면), `padframe`(테두리), `lbtn/rbtn/mbtn`(클릭), `wheel/hwheel/wheel_no_mbtn`(스크롤), `movegrip`(드래그 이동), `menu/minimize/closebtn/blank`, `dragframe_lbtn/rbtn/mbtn`(드래그 상태 버튼), `VK_*`(가상키: CONTROL/SHIFT/MENU/SPACE/TAB/RETURN/BACK/F1~F12/NUMPAD…), 한 글자 키(`C`=Ctrl+C 조합 지원), `assistpad`(보조패드 호출), `tabtip`(터치키보드), `artsize_*`(브러시 크기).
- **제스처 매핑 (설정):** Tap / Swipe 상하좌우 / Pinch in-out 각각에 Left/Right/Middle 클릭·드래그, 키보드 표시/숨김, Sonar, AssistPad 표시, 투명 전환, Shrink, 수직/수평 휠(정/역/1회), 볼륨, 브라우저 앞/뒤, Home/End/PgUp/PgDn, 복사/잘라내기/붙여넣기, Any key…, Run…, Layout… 할당.
- **모드:** 측면 도킹(left/right/top/bottompad), 플로팅(floatpad), 풀스크린(fullscreen + 핀치 줌인/아웃 전환), fingerside(손가락 옆면), ArtistPad(그림용 Ctrl/Shift/Alt/Space+복사/붙여넣기/실행취소 타일), virtualctrls(가상 Ctrl/Shift/Alt).

## 3. 클론 설계 방침 (본 repo)

원본 바이너리 복제 대신 동작·파일호환 클론을 `touchpad_clone/`에 Python(stdlib + ctypes + tkinter, 트레이만 pystray)으로 구현:

- `layout.py`: 원본 INI 섹션/타일 파서 호환 (상대좌표→픽셀 변환, 미지원 타일은 blank/키 폴백)
- `mouse.py`: `user32.SendInput/mouse_event/keybd_event` 래퍼 (상대이동·클릭·휠·가상키)
- `pad.py`: 플로팅/도킹/풀스크린 패드 윈도우 (반투명·최상위·그립 이동·타일 렌더)
- `overlay.py`: Sonar/Track/FakeCursor 시각화 풀스크린 오버레이
- `assist.py`: Ctrl/Shift/Alt/Space 보조 패드
- `settings_ui.py` + `config.py`: 속도/가속도/투명도/제스처/레이아웃 설정 (JSON 저장, 원본 INI 프리셋 로드 가능)
- `tray.py` + `main.py`: 트레이 상주, 시작/종료, 설정 열기
- `presets/`: 원본 문법 호환 기본 프리셋 3종 동봉
- 관리자권한·멀티터치(WM_TOUCH/WM_POINTER)는 MVP에서 제외, 단일 포인터+제스처 근사로 구현. 상용 수준 완성 시 C#/C++ + Pointer API로 이식 권장.
