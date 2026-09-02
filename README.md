# Yes24PDFDumper

YES24 eBook 뷰어에서 **본인이 구매·소장 중인 DRM PDF** 를 원본 그대로 추출하는 .NET 8 도구.

`DOTNET_STARTUP_HOOKS` 로 뷰어 프로세스에 관리형 DLL을 인젝션하고, **Harmony** 로 `Yes24eBook.ViewModels.Viewer.PDFViewModel.getMemFileContent(string) → byte[]` 를 후킹하면 뷰어가 만들어낸 완전히 복호화된 PDF 바이트 배열이 그대로 파일로써 저장합니다.

---

## ✨ 특징

- **원본 PDF 그대로**: 페이지 스크린샷이 아니라 내부에서 가져오는 **원본 파일** 을 획득
- **자동 스캐빈저**: WPF 트리에서 `PDFViewModel` 인스턴스를 찾아, `%APPDATA%\Yes24eBook\.content\` 하위 신간이 뜨면 리플렉션으로 `UnDrmClient.GetMemFileContent()` 능동 호출
- **안티 후킹 우회**: `UnDrmSecurityCoreNet.UnDrmAntiPassAssembly` 의 검사 대상(`FakeAssembly`, `AssemblyInfoLoader`, `AssemblyInfoResult`, `Assembly` 서브클래스) 어디에도 걸리지 않음
- **C++/CLI 우회**: DRM 엔진(`UnDrmClientNet`) 은 C++/CLI mixed-mode 라 Harmony IL rewrite 가 실패 → 순수 C# wrapper 계층인 `PDFViewModel` 에서 후킹

---

## 📋 요구 사항

| 항목 | 버전 |
| --- | --- |
| OS | Windows 10/11 (x64) |
| .NET SDK | 8.0 이상 |
| YES24 eBook 앱 | `C:\Program Files\YES24eBook\` 설치 (경로 다르면 [run.bat](run.bat) 상단 수정) |

---

## 🔨 빌드

```bash
dotnet build YES24Dumper\YES24Dumper.csproj -c Release
```

빌드 결과물: `YES24Dumper\bin\Release\net8.0-windows\YES24Dumper.dll`

---

## 🚀 사용법

1. **본인이 구매한 책을 YES24 eBook 앱에서 미리 다운로드** (앱 재실행 없이 라이브러리에 나타나야 함)
2. 저장소 루트에서 `run.bat` 더블클릭
3. 뷰어가 뜨면 **책 커버를 클릭해 리더를 열기**
4. `dump\` 폴더에 `〈책제목〉.PDF` 가 자동 저장됨

성공 시 로그(`dump\dumper.log`) 마지막:

```
[Dump] GetMemFileContent(".../<uuid>.PDF") -> byte[3975184]  head=25 50 44 46 ...
[Dump]   saved -> C:\...\dump\<uuid>.PDF
[Dump]   ✓ Valid PDF signature — full book extracted.
```

`head=25 50 44 46` (`%PDF`) 가 뜨면 완전 복호화된 원본 PDF 입니다.

---

## 🧭 작동 원리 (요약)

```
run.bat  (DOTNET_STARTUP_HOOKS 세팅)
   ↓
YES24eBook.exe 시작 → .NET 런타임이 YES24Dumper.dll 을 Main() 전에 로드
   ↓
StartupHook.Initialize()
   ├─ 0Harmony.dll 사이드로드 (AssemblyResolve)
   ├─ YES24eBook.dll 로드 감지 후:
   │     ├─ UnDrmClientNet.dll 프리로드 (PDFViewModel 시그니처 해석용)
   │     └─ Harmony.Patch(PDFViewModel.getMemFileContent) postfix
   └─ 백그라운드 스캐빈저 시작
         ├─ WPF PresentationSource.CurrentSources 순회
         │    → DataContext 트리에서 PDFViewModel 인스턴스 캐치
         └─ %APPDATA%\Yes24eBook\.content\<uuid>\*.PDF 감시
               → 발견 시 인스턴스.drmClient.GetMemFileContent(경로) 리플렉션 호출
               → 반환된 byte[] 를 dump\〈원본이름〉 로 저장
```

**hook 우회 대상은 순수 C# wrapper**, **C++/CLI native 호출은 리플렉션으로 우회**
Yes24EBook이 .NET 런타임이 아닌 구축으로 전환하거나 DOTNET_STARTUP_HOOKS 를 막는것으로 패치 가능.
---

## 🗂 프로젝트 구조

```
Yes24PDFDumper/
├── YES24Dumper/
│   ├── YES24Dumper.csproj    # .NET 8 클래스 라이브러리
│   ├── StartupHook.cs         # DOTNET_STARTUP_HOOKS 진입점
│   └── PdfPatches.cs          # Harmony 후킹 + 스캐빈저
├── run.bat                     # 환경변수 세팅 후 뷰어 실행
├── LICENSE                     # MIT
└── README.md
```

---

## 🧪 안 될 때 진단

| 증상 | 원인 / 조치 |
| --- | --- |
| `Hook DLL not found` | `dotnet build ... -c Release` 먼저 실행 |
| `EXE not found` | [run.bat](run.bat) 상단 `YES24_INSTALL_DIR` 수정 |
| `Preloaded ...` 만 뜨고 그 다음 로그 없음 | YES24eBook.dll 이 아직 로드 안 된 상태. 책 커버를 실제로 클릭해서 리더를 열어봐야 함 |
| `[Dump]` 라인이 안 뜸 | 백그라운드 스캐빈저가 인스턴스를 못 잡음. 리더 완전히 열린 뒤 30초 정도 대기 |
| `head` 가 `25 50 44 46` 이 아님 | native `undrm_helper_get_content_wide` 내부에 추가 스크램블 계층 존재. issue 로 보고 필요 |

### ⚠️ 사용 시 유의

- 이 도구는 **본인이 정당하게 구매한 콘텐츠** 를 개인 백업·기기간 이전 등의 목적으로 사용하는 것을 전제로 합니다
- 추출한 파일의 **재배포·업로드·공유는 저작권법 위반** 이며, 이 도구 배포자와 무관합니다
- 사용자는 자신이 속한 국가/지역의 저작권법 및 YES24 이용약관을 준수할 책임이 있습니다
- 사용으로 발생하는 어떤 결과에도 저자는 책임지지 않습니다

---

## 📜 License

MIT License — 자세한 내용은 [LICENSE](LICENSE) 참고.
