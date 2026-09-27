# TouchMousePointer 클론 (가상 트랙패드)

원본: `C:\Program Files\TouchMousePointer` (Lovesummertrue, v3.0.1.2) 분석 기반 호환 클론.
자세한 분석은 [ANALYSIS.md](ANALYSIS.md), 전체 메뉴얼·구조도는 [docs/manual.html](docs/manual.html) 참조.

## 현행: v2 (C#/WPF, `cs/TouchPadCloneV2/`)

**입력은 Raw Input으로 디지타이저를 직접 읽습니다** (`Core/RawTouchInput.cs`).
원본 `TouchMousePointer.exe`가 `RegisterRawInputDevices`/`GetRawInputData`/`GetRawInputDeviceInfoW`로
디지타이저를 직접 읽는 것과 같은 계열입니다. 이유와 경위:

- **WPF 터치는 승격(promotion)됩니다** — 프레임워크가 터치를 스타일러스로, 다시 마우스로 승격하고,
  그 과정에서 시스템이 **커서를 접촉점으로 끌어당깁니다.** 이 프로젝트의 어려운 문제 대부분이 여기서 나옵니다:
  드래그 중 커서가 패드로 끌려감, 유령 마우스 이벤트, 드래그가 두 창으로 갈라짐, 가상 커서가 필요해진 이유.
- **Raw Input은 hit-test와 무관하게 전달됩니다**(`RIDEV_INPUTSINK`). 그래서 "창을 click-through로 두고
  입력은 그대로 받는" 원본의 구조가 가능해집니다. hit-test로 입력을 받으면 창을 투명하게 만들 수 없습니다.
- **커서 이동은 `SetPhysicalCursorPos`(물리 픽셀), 클릭은 `mouse_event`** — 원본이 쓰는 프리미티브 그대로입니다
  (`SendInput` 아님). 주입 입력에는 태그(`dwExtraInfo`)를 붙여 자기 입력임을 구분합니다(원본은 `GetMessageExtraInfo`).
- 디지타이저 HID 리포트는 `HidP_GetCaps`/`HidP_GetValueCaps`/`HidP_GetUsageValue`로 파싱합니다
  (Contact Identifier `0x51`, Tip Switch `0x42`, In Range `0x32`, X `0x30`, Y `0x31`).
  **주의: `RAWHID.bRawData`는 포인터가 아니라 내장 배열(`BYTE[1]`)** — 리포트 바이트는 `dwSizeHid`/`dwCount` 뒤
  오프셋 8에서 시작합니다. 포인터로 선언하면 리포트 내용을 주소로 읽어 접근 위반(0xc0000005)으로 죽습니다.
- 좌표: 터치 **스크린**(usage `0x04`)은 절대 좌표 → 화면 px, 터치 **패드**(usage `0x05`)는 자체 0..1 표면 →
  패드 사각형에 배치합니다.

WPF **터치 이벤트**(손가락별 추적) 경로도 남아 있습니다(`UseRawPointer`) — raw input이 실기에서
검증되면 그때부터 창을 상시 click-through로 만들고 컨트롤을 투명 영역 밖에 배치할 수 있습니다.

```bat
cd cs\TouchPadCloneV2
dotnet build
dotnet run
```

- 플로팅 가상 터치패드: **한 손가락 = 커서만**(이동·탭·홀드·홀드 후 드래그), **제스처 동작은 손가락 2개 전용**(세로 스크롤, 가로 스와이프 내비게이션, 두손가락 탭)
  - 1손가락 스와이프 동작은 의도적으로 제거됨: 패드를 세게 긁으면 커서를 옮기는 대신 스크롤이 발해져서 원하면 안 되는 제스처가 커서 조작을 차지함
- 좌/우/중 버튼 + 드래그 락, 세로/가로 휠 타일, `VK_*`/한글자 키 타일(Ctrl 콤보 포함), 보조패드
- 홀드 판정: **순이동이 아니라 "최근 150ms 이동률(peak rate)"** 기준. 이 패드는 손가락을 가만히 둬도
  초당 ~0.4 DIP(=1 m/s)의 크리프가 보고되며 500ms 안에 130~215 DIP 누적됩니다(실측).
  거리 기준으로 판정하면 크리프가 모든 임계값을 0.2초 안에 통과해 **홀드가 100% 취소**됩니다.
  실측 대비 정지 시 65 DIP/150ms ↔ 실제 드래그 375 DIP/150ms 로 5.8배 격차.
  - 판정되면(peak latch) 그 접점은 드래그 확정 → 제스처 없음
  - **커서를 멈추는(pin) 방식은 쓰지 않음**: 이 패드의 정상 이동 속도(0.33~0.81 DIP/ms)가 크리프(0.43)와 구분되지 않아, 핀 임계값을 어떻게 잡아도 "해제 안 됨(=패드 0.5초 동안 죽음)" 또는 "크리프에 즉시 해제" 중 하나. 즉시 이동을 우선하고, 홀드 중 커서 유동은 설정 `HoldDamp`(기본 1=끔쇠 없음, 낮추면 홀드 중 감쇠) 선택형으로 제공
  - 스킵/이동 시 40ms 폴링 **재암**되므로 제스처가 조용히 사라지지 않음
  - 임계값은 설정 슬라이더 "홀드 중 드래그로 간주하는 이동 (DIP/150ms)" 로 조정 (기본 100)
- 2번째/3번째 누름 = **짧은 정지 후 판별**: 움직이면 드래그(원자적 press, 조준점 고정), 가만히 있으면 `길게 누르기` 동작(우클릭)
- **물리 마우스 존중 모드(기본)**: 합성 클릭/드래그/휠이 실제 커서를 옮긴 뒤 **물리 마우스가 두던 자리로 복구**하고, 물리 마우스 입력이 가상 커서를 조종하지 않음.
  (설정 → General → 물리 마우스 = "통합" 선택 시 기존 동작: 실제 커서를 가상 커서 아래 고정, 호버가 항상 가상 커서를 따라감)
- 원본 INI 프리셋 호환 로더 — 원본 `Preset *.ini` 직접 열기 가능 (`cs/PresetCheck`로 56종 전수 검증)
- 상단 ModeStrip(터치 시 확장), 트레이 상주, 설정 저장 (`%APPDATA%\TouchPadClone\settings.json`, v1과 공유)
- 진단: `set TOUCHPAD_DEBUG=1` → `%TEMP%\touchpad_v2.log`에 터치 원신호 기록
- 미구현: 커서 시각화 오버레이(sonar/track/fake), 핀치 제스처, vJoy 축 에뮬, `artsize_*`

## 모드 전환 (원본 대응 + 클론 확장)

원본에는 **화면 상단 전용 스와이프 영역이 없다.** 원본의 모드 전환 수단은:
1. 패드 위 **핀치 아웃 = 풀스크린 / 핀치 인 = 창모드 복귀** (공식 스크린샷 4→7번)
2. 작업표시줄 **트레이 아이콘 탭** (램프 점등 = 풀스크린 ON)
3. 제스처에 할당하는 **Layout… 전환**, 패드의 **menu 타일**

클론은 2·3과 menu 타일을 지원하고, 요청받은 상단 스와이프를 위해
추가로 **ModeStrip**(화면 상단 중앙 바, 터치하면 확장)을 상시 표시한다:
- 좌/우로 밀기 = 이전/다음 레이아웃 (`floatpad → leftpad → …`)
- 탭 = 설정 열기, 아래로 밀기 = 풀스크린 토글

## vJoy (가상 조이스틱) 설치 안내

원본의 `VJoy64.dll`은 Shaul Efraim의 오픈소스 **vJoy** SDK로,
`Preset WASD gaming.ini`가 게임에 조이스틱 신호를 보내기 위해 쓴다.
vJoy는 드라이버라서 동봉이 안 되고 **별도 설치**가 필요하다:

- 공식 사이트: http://vjoystick.sourceforge.net
- 다운로드: https://sourceforge.net/projects/vjoystick/files/
- GitHub: https://github.com/shauleiz/vJoy
- Windows 10/11 최신 빌드용 유지 포크: https://github.com/jshafer817/vJoy

v2 설정 창의 "게임 프리셋용 vJoy" 행에서 설치 상태 확인 + 바로 열기.
vJoy 없이도 WASD 프리셋은 **키보드 입력으로 대체 동작**한다.

## 원본과의 차이

- 핀치 줌인/아웃 전환은 ModeStrip 아래밀기로 대체 (핀치 제스처 미구현)
- vJoy 축 에뮬 미포함, WebView2 설정·스토어 라이선스 미포함
- 커서 시각화 오버레이는 v1에만 있음 (v2 이식 예정)
