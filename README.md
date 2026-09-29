# Claude 사용량 위젯

## 단일 실행 파일

**[artifacts/standalone/ClaudeUsageWidget.exe](artifacts/standalone/ClaudeUsageWidget.exe)** 하나만 복사해서 실행할 수 있습니다. Windows x64용이며 .NET 런타임을 포함합니다. 별도 DLL, 설치 프로그램, Chrome 확장 프로그램은 필요하지 않습니다. 기존 폴더 배포본과 기능 및 설정 저장 위치가 같습니다.

다시 패키징하려면 `powershell -NoProfile -ExecutionPolicy Bypass -File .\build-single.ps1`을 실행합니다. 최초 빌드에서는 공식 NuGet 저장소에서 필요한 런타임 패키지를 프로젝트의 `artifacts` 폴더로 내려받습니다. 배포 파일은 한 개지만 실행 시 일부 런타임 파일은 Windows 임시 폴더에 풀리고 설정과 암호화 인증은 기존 `%LOCALAPPDATA%/ClaudeUsageWidget`에 저장됩니다. 패키징 방식은 [Microsoft의 단일 파일 배포 문서](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)를 따릅니다.

> **현재 배포본은 v0.4 Claude Code / 브라우저 연결 방식입니다.** `start.cmd`를 실행하고 **연결**을 누릅니다. Claude Code 인증을 우선 확인하고, 사용할 수 없으면 평소 기본 브라우저에서 로그인합니다. 확장 프로그램은 필요하지 않습니다. 최신 설명은 [AUTHENTICATION.md](AUTHENTICATION.md)를 따라 주세요.

기본 30초 갱신, 투명도, 항상 위에 표시, 직접 입력과 초기화 카운트다운을 지원합니다. Fable의 `limits[].percent` 및 `scope.model.display_name` 응답 형식도 지원합니다. 실제 Claude Code 인증으로 5시간과 Fable 사용량 수신을 확인했습니다. 신규 브라우저 로그인은 로컬 콜백과 가짜 서버로 검증했으며 실제 서버 로그인은 아직 확인하지 않았습니다.

## 이전 v0.2 기록

아래의 전용 Chrome 프로필, DevTools 및 로그아웃 설명은 이전 구현에 대한 기록입니다. 현재 v0.4에는 적용되지 않습니다.

Windows 바탕 화면에서 Claude의 5시간 사용률과 Fable 사용률, 5시간 한도 초기화까지 남은 시간을 보여 주는 C# / WPF 앱입니다.

## 실행

현재 폴더의 **`start.cmd`를 더블 클릭**하거나 `artifacts/app/ClaudeUsageWidget.exe`를 실행합니다.

1. **로그인** 버튼을 누르면 PC에 설치된 일반 **Chrome**이 열립니다. Chrome이 없으면 **Edge**를 사용합니다.
2. 열린 브라우저에서 Claude 계정으로 로그인합니다. Google 로그인도 이 일반 브라우저에서 직접 진행합니다. 조직이 여러 개라면 사용할 조직을 선택합니다.
3. 로그인 후 위젯이 사용량을 확인하면 **자동으로 연결**하고 브라우저 창을 최소화합니다. ‘위젯으로 돌아가기’ 버튼을 누를 필요가 없습니다.
4. **설정**에서 투명도, 항상 위에 표시, 조회 주기를 변경합니다.

**v0.2부터 WebView2 내장 로그인 창을 사용하지 않습니다.** 기존 브라우저와 별도로 위젯용 프로필을 사용하므로 최초 한 번은 새로 열린 Chrome/Edge 창에서 로그인해야 합니다. 기존 개인 프로필의 쿠키를 복사하거나 복호화하지 않습니다.

자동 조회 중에는 위젯용 브라우저의 Claude 탭을 **최소화해 두세요.** 브라우저를 닫으면 다음 조회에서 저장된 프로필로 다시 실행합니다. 세션이 남아 있으면 재로그인 없이 연결됩니다. 위젯을 종료하면 전용 브라우저도 종료하며, 기존 개인 브라우저 창은 그대로 유지합니다. 이메일 링크로 인증한다면 링크가 위젯용 브라우저 창에서 열려야 해당 세션이 연결됩니다.

## 기능

| 항목 | 동작 |
| --- | --- |
| 5시간 사용량 | 서버에서 받은 사용률을 표시합니다. |
| Fable 사용량 | 응답에서 Fable로 식별되는 항목을 표시합니다. 없으면 항목이 없다고 안내합니다. |
| 초기화 카운트다운 | 서버 초기화 시각을 기준으로 매초 계산합니다. PC의 현지 시간대로 시각을 표시합니다. |
| 갱신 주기 | 기본 30초입니다. 10~86,400초 범위의 정수를 입력합니다. |
| 투명도 | 0~80%입니다. 0%는 불투명하며 기본값은 6%입니다. 설정 창의 슬라이더로 미리 확인합니다. |
| 항상 위 | 기본으로 켜져 있습니다. 상단의 고정 버튼이나 설정에서 전환합니다. |
| 직접 입력 | 두 사용률을 0~100%로 입력하고 초기화 시각도 지정할 수 있습니다. 자동 조회는 중지됩니다. |
| 위치 이동 | 상단의 Claude 제목 부분을 드래그합니다. 종료할 때 위치를 저장합니다. |
| 로그아웃 | 설정에서 위젯용 브라우저 프로필의 쿠키와 Claude 사이트 데이터를 삭제합니다. 기존 개인 브라우저 프로필에는 영향을 주지 않습니다. |

사용량은 지정한 주기마다 조회하며 카운트다운만 초 단위로 갱신합니다. 초기화 시간이 지나도 임의로 0%를 표시하지 않고 새 서버 응답을 기다립니다. 실패하면 마지막 확인값과 조회 시각을 표시합니다. 요청 제한이나 연결 오류가 발생하면 재시도 간격을 늘립니다.

## 로그인과 데이터

- 로그인은 설치된 일반 Chrome/Edge가 처리합니다. 비밀번호나 세션 쿠키를 앱 설정이나 로그에 저장하지 않습니다.
- 위젯은 전용 브라우저가 연 임의 포트의 `127.0.0.1` DevTools 연결을 사용합니다. 연결 대상 식별자와 포트를 대조하며 외부 주소나 다른 포트로 연결하지 않습니다.
- DevTools 연결은 같은 PC에서 실행되는 프로세스를 신뢰하는 방식입니다. 전용 프로필은 Claude 연결 용도로 사용하며, 위젯 종료 시 해당 브라우저를 종료해 연결 포트도 닫습니다.
- 사용량 조회는 로그인된 `https://claude.ai` 탭 안에서 실행합니다. 브라우저가 인증 쿠키를 직접 첨부하며, 위젯에는 사용량 결과만 반환합니다. Google 인증 페이지에는 조회 스크립트를 실행하지 않습니다.
- 설정은 `%LOCALAPPDATA%/ClaudeUsageWidget/settings.json`에 저장합니다.
- 새 브라우저 프로필은 `%LOCALAPPDATA%/ClaudeUsageWidget/BrowserSessions/Chrome` 또는 `Edge`에 저장합니다. 구 버전 WebView2 프로필은 새 인증에 사용하지 않습니다.
- 앱이 전송하는 조회 대상은 Claude 웹 서비스입니다. 로그인 과정에서는 선택한 인증 제공자의 페이지가 열릴 수 있습니다. 별도의 분석·텔레메트리 서버는 없습니다.

## 조회 방식과 제한

Anthropic은 [설정의 사용량 화면에서 5시간 및 주간 사용 한도를 확인하는 방식](https://support.claude.com/en/articles/9797557-usage-limit-best-practices)을 안내합니다. 이 앱은 브라우저 세션을 사용해 Claude 웹 화면의 내부 `/api/organizations/{organizationId}/usage` 응답을 조회합니다. **공식적으로 보장된 공개 API를 사용하는 앱은 아니므로 웹 서비스 변경에 따라 연동 수정을 해야 할 수 있습니다.**

Fable 응답 형식은 계정과 서비스 변경에 따라 달라질 수 있습니다. 현재는 이름에 `fable`이 있는 최상위 버킷과 `model_scoped` / `limits` 배열의 Fable 이름을 인식합니다. 이름이 없는 전체 주간 한도나 추가 사용량을 Fable로 추정하지 않습니다. 인식하지 못하면 직접 입력 모드를 사용할 수 있습니다. Claude 계정 로그인 후 실제 자동 조회가 되는지는 별도로 확인해야 합니다.

## 개발 및 검증

개발 환경은 Windows 10/11과 .NET 10 SDK입니다. 실행에는 .NET 10 Desktop Runtime 및 일반 Chrome/Edge가 필요합니다. WebView2와 별도의 NuGet 패키지는 필요하지 않습니다.

```powershell
dotnet build ClaudeUsageWidget -c Release
dotnet run --project ClaudeUsageWidget.Tests -c Release
node --test ClaudeUsageWidget.Tests/browser.test.cjs
dotnet run --project ClaudeUsageWidget.Tests -c Release -- --browser-integration
dotnet run --project ClaudeUsageWidget -c Release -- --smoke-test
dotnet publish ClaudeUsageWidget -c Release --self-contained false -o artifacts/app
```

`build.ps1`은 핵심 로직 및 브라우저 스크립트 검증과 배포 빌드를 순서대로 실행합니다. 스크립트 검증에는 Node.js가 필요합니다. 화면 검증 모드는 실제 계정에 접속하지 않으며, 샘플 데이터로 `artifacts` 폴더에 화면 이미지를 생성합니다. 사용자 설정을 읽거나 저장하지 않습니다.

`--browser-integration`은 `artifacts` 아래의 별도 테스트 프로필에서 일반 Chrome을 화면 없이 실행합니다. `about:blank`에서 실제 DevTools 연결, Promise 반환, 재연결, 가짜 테스트 쿠키 삭제를 확인하고 테스트 브라우저를 종료합니다. 실제 계정은 사용하지 않습니다.

구현 참고 문서는 [Chrome의 전용 프로필 요구 사항](https://developer.chrome.com/blog/remote-debugging-port?hl=en)과 [Microsoft Edge DevTools Protocol](https://learn.microsoft.com/en-us/microsoft-edge/devtools/protocol/)입니다. Google 로그인 최종 성공 여부는 사용자가 실제 계정으로 확인해야 하며, 별도의 조직 정책이나 보안 확인이 있다면 브라우저 안내를 따라야 합니다.

구조는 `ClaudeUsageWidget`(WPF 화면·브라우저 연결), `ClaudeUsageWidget.Core`(파싱·시간 계산·설정 저장), `ClaudeUsageWidget.Tests`(핵심 로직과 브라우저 조회 검증)로 나뉩니다.
