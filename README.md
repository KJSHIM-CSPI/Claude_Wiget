# Claude 사용량 위젯

Windows 바탕 화면에서 Claude의 5시간 사용량, Fable 사용량과 초기화까지 남은 시간을 표시하는 C# / WPF 앱입니다. 현재 버전은 v0.4.1입니다.

## 실행

배포용 단일 파일은 `artifacts/standalone/ClaudeUsageWidget.exe`입니다. Windows x64용이며 .NET 런타임을 포함하므로 EXE 하나만 복사해서 실행할 수 있습니다. Git에는 빌드 결과가 포함되지 않습니다. 배포된 파일이 있다면 [GitHub Releases](https://github.com/KJSHIM-CSPI/Claude_Wiget/releases)에서 받을 수 있습니다.

프로젝트 폴더에서는 `start.cmd`로 실행합니다. `build.ps1`로 만든 폴더 배포본이 있으면 사용하고, 없으면 .NET SDK로 소스를 실행합니다.

**연결**을 누르면 Claude Code 인증을 먼저 확인합니다. 사용할 수 없으면 저장된 위젯 인증을 사용하거나 평소 기본 브라우저에서 로그인합니다. Chrome 확장 프로그램과 별도 브라우저 프로필은 필요하지 않습니다. 연결 절차와 인증 보관 방식은 [AUTHENTICATION.md](AUTHENTICATION.md)를 참고하세요.

## 기능

| 항목 | 동작 |
| --- | --- |
| 사용량 | 5시간 및 Fable 사용률을 표시합니다. 응답에 없는 항목은 임의로 추정하지 않습니다. |
| 초기화까지 남은 시간 | 서버가 알려 준 시각을 기준으로 매초 갱신합니다. |
| 조회 주기 | 기본 30초이며 10~86,400초 범위에서 입력합니다. 서버 요청 제한 시 대기 시간을 우선합니다. |
| 투명도 | 0~80%를 조절합니다. 기본값은 6%입니다. |
| 항상 위 | 상단의 고정 버튼 또는 설정에서 전환합니다. |
| 직접 입력 | 두 사용률과 초기화 시각을 직접 입력하며 자동 조회는 중지합니다. |
| 연결 해제 | 위젯 인증을 삭제하고 자동 연결을 중지합니다. Claude Code와 브라우저 로그인은 유지합니다. |
| 위치 이동 | 상단 제목을 드래그합니다. 종료 시 위치를 저장합니다. |

설정 및 암호화된 인증은 `%LOCALAPPDATA%/ClaudeUsageWidget`에 저장합니다. 연결 해제 후에는 새로고침이나 재실행으로 연결되지 않으며 **연결** 버튼으로 재개합니다.

## 빌드

Windows와 .NET 10 SDK가 필요합니다. Node.js, 브라우저 확장 및 Native Messaging Host는 사용하지 않습니다.

핵심·인증 검사를 실행하고 일반 폴더 배포본을 생성합니다. 이 배포본은 실행할 PC에 .NET 10 Desktop Runtime이 필요합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

.NET 런타임을 포함한 Windows x64 단일 EXE를 생성합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-single.ps1
```

단일 EXE 빌드는 공식 NuGet 저장소에서 런타임 패키지를 내려받아 `artifacts` 아래에 캐시합니다. 실행 시 일부 런타임 파일은 Windows 임시 폴더에 풀립니다. [Microsoft의 단일 파일 배포 문서](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)를 따릅니다.

## 검증 및 폴더 구성

```powershell
dotnet run --project ClaudeUsageWidget.Tests -c Release
dotnet run --project ClaudeUsageWidget -c Release -- --smoke-test
```

일반 검사는 가짜 인증과 응답을 사용하며 실제 계정에 접속하지 않습니다. 화면 검증 결과는 `artifacts`에 생성됩니다. 실제 계정 조회는 테스트 실행 시 명시적으로 `--oauth-probe`를 지정할 때만 수행합니다.

- `ClaudeUsageWidget`: WPF 화면입니다.
- `ClaudeUsageWidget.Core`: OAuth, 인증 저장, 사용량 해석, 시간 계산 및 설정입니다.
- `ClaudeUsageWidget.Tests`: 핵심 동작과 인증 검사입니다.
- `artifacts/app`: 일반 폴더 배포본입니다.
- `artifacts/standalone`: 배포용 단일 EXE입니다.
- `bin`, `obj`, `artifacts/nuget-*`: 재생성 가능한 빌드 결과와 패키지 캐시입니다. Git 업로드 대상에서 제외합니다.

검증 이력은 [VALIDATION.md](VALIDATION.md)에 있습니다. 기존 Claude Code 인증을 통한 실제 사용량 조회는 확인했으며, 신규 브라우저 로그인은 로컬 콜백과 가짜 서버로 검증했습니다. 신규 브라우저 로그인의 실제 서비스 성공 여부는 아직 확인하지 않았습니다. 공개 문서가 없는 OAuth 사용량 응답을 사용하는 비공식 개인용 도구이므로 서비스 변경에 따라 수정이 필요할 수 있습니다.