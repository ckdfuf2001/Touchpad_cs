# TouchPadCloneRaw 재구현 문서 (관측 rig 기준)

목표: 터치스크린(Novatek HID) 위에 뜨는 가상 트랙패드. 원본(TouchMousePointer
v3.0.1.2) 동작을 측정해서 그대로 구현한다. 추측 금지, 측정만.

현재 상태: **관측 rig**. 출력(주입)은 전부 무력화, 인식/로그/HUD만 동작.
터치하면 시스템에 아무 일도 안 생기고, 로그+HUD로만 보인다.

## 1. 프로젝트/파일

- `cs/TouchPadCloneRaw/` : 현행 엔진 (.NET 10, WPF). 구 `cs/TouchPadCloneV2`(v1)는
  git에서 삭제, `v1-legacy` 태그로 보존.
- `Core/RawTouchInput.cs` : HID raw input 파서. 리포트→접촉 슬롯 파싱,
  `Contact(id, screenX, screenY, isDown)` 콜백. 엔진 소스(70Hz).
- `Core/HidTouch.cs`, `Core/RawPointer.cs` : raw input 등록/프로브 구조체.
- `Core/InputSim.cs` : 출력층 choke point. mouse_event/keybd_event/
  SetPhysicalCursorPos/SetCursorPos 계열 전부 여기. 주입에는 InjectTag
  (`0xFF515700|tid`)를 찍어서 외부 probe와 구분.
- `Core/AppSettings.cs` : `%APPDATA%\TouchPadClone\settings.json`.
  LongPressMs=500, MultiTapMs=900, DragStartDip=30, Speed, 제스처맵 3종.
- `Core/Preset.cs` : INI 타일 파서. `tileNNN=TYPE,x,y,w,h` (0..100 상대좌표).
- `Core/DebugLog.cs` : `%TEMP%\touchpad_v2.log` (시각 HH:mm:ss.fff).
- `Core/NoActivate.cs` : NoActivate + TabletTweaks(관측 중 OFF).
- `PadWindow.xaml(.cs)` : 패드 본체. 입력→모델→출력 파이프라인 전부.
- `presets/Default.ini` : floatpad 등 레이아웃 (pad/lbtn/rbtn/wheel/VK_*).

## 2. 초기화 순서

1. `App.OnStartup` → 설정 Load → PadWindow 생성.
2. PadWindow ctor: RestoreCursor(무력화됨), NoActivate, HUD 타이머(100ms),
   라벨 클릭→로그열기, LocationChanged/SizeChanged→제목 갱신.
3. `SourceInitialized` : WndProc 훅(PadWndProc: raw 0x00FF→HID 파서),
   topmost 타이머(1s), ghost sweep(3s).
4. Loaded: fake 초기값=주화면 중앙, 제목에 위치·크기 표시.
5. HID Attach: Digitizer usage 등록 → Contact 콜백 연결:
   패널좌표→screen 매핑 D(swap+Yflip, 코너탭 18px 검증) → `/dpi` DIP →
   윈도우상대 local → `ForwardRawTouch(id+1000)` 로 WPF 터치 이벤트 합성.
   pointer 경로는 소비만(포워드 없음, 더블 방지).

## 3. 입력 파이프라인 (접촉 1개 기준)

DOWN(합성 TouchDown) → 거부검사(MoveOwnsSession) → HitTest(타일) →
리사이저 분기(하단 모서리 26px: 창 리사이즈, 제스처 분리) →
other-zone 하드블록(리턴, UP UNKNOWN만 남음) →
첫손가락이면 커서를 터치점에 착륙(MoveAbsolute, 큐経由) →
Finger 생성·등록 → 세션 시작 → 상태/zone 표시.
MOVE → fingers 조회 → dx,dy → 모델(fake) 갱신 → 상대델타 큐 전송.
UP → finger 제거 → 날것 기록(ms, moved) → 세션 종료/OUTCOME.

## 4. 좌표계 (헷갈리면 여기)

- 패널 DIP: 터치 위치 (WPF DIP).
- 스크린 DIP: Left/Top 기준. `ScreenOf(p) = p + (Left,Top)`.
- 물리 px: DIP × _dpi (2.25). InputSim 입출력은 물리 px.
- 큐 절대좌표: 0..65535 가상스크린 정규화 (음수 origin 포함).
- 모델(fake): press-start + 이동×dpi×gain. 절대동기 없음(상대기기).
- 읽기 주의: 터치 중 GetPhysicalCursorPos가 실패해 (0,0)을 뱉음.
  (0,0) 읽음은 버린다. Cursor()는 쓰기/읽기 fallback 체인.

## 5. 출력층 (현재 무력화 상태)

- `OutputEnabled=false` : mouse_event/keybd_event/SetPhysicalCursorPos/
  ShowCursor 전무. 버튼/키 함수는 WOULD 로그만.
- `MoveOutputEnabled=true` : 이동만 큐로 나감 (상대 MOVE + 절대 배치).
- 가시성 wiggle(ClearSuppression, ±1px 왕복)은 출력이 아니라 유지.
- 복구: 플래그 2개 true로 (한 줄씩).

## 6. 인식 로직 (현재 전부 스텁, git에 원본 있음)

제거된 것(메서드 입구 return, 호출부는 그대로):
ArmLong/ArmHold(타이머), StartDrag/GrabHold/FirePressAction,
PressDown/PressUp/ReleaseActionButton, FireWheel,
DoGesture(assist_pad만 통과).
살아있는 것: DOWN/UP/MOVE 날것 로그, fingers/세션/모델 bookkeeping,
zone 판정+표시, wheel 누적 WOULD, HUD, 타이틀/리사이저/크롬 창조작.

## 7. 영역 (rebuild v1)

- `ZoneOf(tile, point)`: resizer(하단모서리) / left-click(lbtn) /
  right-click(rbtn) / wheel / other(나머지 전부).
- other: 하드블록. resizer: 터치 리사이즈(메시지좌표 누적 방식).
- title: 터치 윈도우 드래그(메시지좌표 델타, 데드밴드 1px).
- 좌하단 윗줄(ZoneLabel)에 터치 영역 표시. 탭 WOULD/휠 WOULD 로그.

## 8. HUD/로그

- 좌하단: 예상(터치 intake + 상태). 클릭하면 로그 파일 열림.
- 우하단(100ms 폴): `L:up R:up P(물리) G(논리) sup?` = 날것만.
- 클릭해도 로그 열림. sup = OS 커서 숨김 상태.
- 세션 OUTCOME raw / TOUCHDOWN / TOUCHUP / PERF / RAWINPUT contact 로그.

## 9. 측정된 장치 사실 (Novatek 터치스크린)

- 패널이 쎈 누름을 접촉 2개로 보고(덩어리 분리). 40DIP 이내=1손가락 병합.
- 커서 끌어당김은 DOWN/떼기 때 이산적. 이동 중에는 우리 쓰기가 먹음.
- suppression은 일시적(스스로 풀림). 큐 이동이 가시성 유지.
- TouchDevice.Id는 접촉마다 증가, 다 떼면 1부터 재사용.
- 싱글프레스 홀드=우클릭(500ms), 체인 윈도우 900ms.

## 10. 목표 스펙 4종 (재빌드 시)

1. 톡+뗌: L Down+Up. 2. 톡+꾹: R Down+Up.
3. 톡톡+뗌: 더블클릭. 4. 톡톡+유지: L Down 유지, 이동=드래그, 떼면 up.
   (2nd/3rd press 즉시눌림 아님. 0.5초 홀드가 발화. 클릭 먼저 없음.)

## 11. git 지도

- main HEAD: 관측 rig + 영역/HUD (계속 전진).
- `v1-legacy`: 구 v1 최종.
- stash 3개: 09-29 02:48(2AM 작업중), WIP 09-30 23:32, 최신(타이머 모델).
- `.git/info/exclude` 에 `/*` 있음: `git add -f` 필수. bin/obj/dist 커밋 금지.
- 가끔 `.git/index.lock` 잔류 → 지우고 재시도.

## 12. 빌드/실행

```bat
cd cs\TouchPadCloneRaw
dotnet build -c Release
bin\Release\net10.0-windows\TouchPadCloneRaw.exe
```
실행 중 바이너리 덮어쓰기 불가 → 선 kill 후 빌드. 실행경로는
`Get-CimInstance Win32_Process` 로 확인 (mutex 없음: 중복실행 금지).
