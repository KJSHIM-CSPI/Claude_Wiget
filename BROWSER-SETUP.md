# 연결 안내

**v0.4부터 확장 프로그램이 필요하지 않습니다.** `start.cmd`를 실행하고 **연결**을 누르면 Claude Code 인증을 먼저 확인합니다. 사용할 수 없으면 기본 브라우저에서 로그인합니다. 최신 연결 절차와 인증 정보 보관 방식은 [AUTHENTICATION.md](AUTHENTICATION.md)를 따라 주세요.

## 이전 Chrome 연결 v0.3 기록

아래 설치 절차는 현재 버전에서는 필요하지 않습니다.

위젯 전용 로그인 창에서 Cloudflare 확인이 반복되는 문제를 피하기 위해 **평소 사용하던 Chrome에 설치한 확장 프로그램**으로 연결합니다. v0.3 위젯은 디버깅 Chrome을 실행하거나 별도 로그인 프로필을 만들지 않습니다.

## 최초 설정

1. 기존 위젯을 종료하고 `start.cmd`로 v0.3을 실행합니다. 상단에 `usage 0.3`이 표시되는지 확인합니다.
2. 배포 폴더 `artifacts/app/setup-browser.cmd`를 한 번 실행합니다. 현재 Windows 사용자의 Chrome에 위젯 통신 호스트를 등록합니다. 관리자 권한은 필요하지 않습니다.
3. **평소 Chrome**에서 `chrome://extensions`를 열고 개발자 모드를 켭니다. **압축해제된 확장 프로그램을 로드합니다**를 눌러 `artifacts/app/extension` 폴더를 선택합니다.
4. 평소 Chrome의 로그인된 `https://claude.ai/settings/usage` 탭을 선택합니다. Chrome 확장 프로그램 메뉴에서 **Claude 사용량 위젯 연결**을 열고 **이 탭 연결 / 조회 재개**를 누릅니다.
5. 위젯의 **새로고침**을 누릅니다. 연결되면 기본 30초마다 자동 조회하고 초기화까지 남은 시간은 매초 갱신합니다.

Chrome과 선택한 Claude 탭을 열어 두어야 합니다. 최소화는 가능합니다. Chrome을 완전히 종료한 뒤 다시 켰다면 탭을 다시 선택해 연결합니다. 위젯만 재시작하면 확장이 30초 간격으로 다시 연결합니다.

보안 확인이나 로그인 만료가 감지되면 웹 요청을 중단합니다. 평소 Chrome에서 직접 확인을 끝낸 뒤 확장의 **이 탭 연결 / 조회 재개**를 누릅니다. HTTP 429의 재시도 대기 시간은 이 버튼을 눌러도 유지됩니다. 보안 확인을 자동으로 누르거나 우회하는 기능은 없습니다.

## 등록 범위와 해제

설치 스크립트는 다음 현재 사용자 레지스트리 키의 기본값만 배포 폴더의 호스트 매니페스트 경로로 설정합니다.

`HKCU\Software\Google\Chrome\NativeMessagingHosts\com.claude_usage_widget.bridge`

Chrome 정책, 기존 로그인, 개인 프로필과 쿠키를 변경하지 않습니다. 확장 권한은 Claude 사이트, 스크립트 실행, 로컬 상태 저장, 재연결 알람, Native Messaging입니다. 위젯으로 전달되는 계정 데이터는 **5시간/Fable 사용률과 초기화 시각**뿐입니다. 조직 식별자는 브라우저 안에서 조회에만 사용합니다.

해제하려면 Chrome 확장 관리 화면에서 확장을 제거하고 배포 폴더에서 다음 명령을 실행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup-browser.ps1 -Remove
```

위젯 설정의 **조회 중지**는 자동 조회만 중지합니다. Chrome 로그인은 유지됩니다.

## 제한과 확인 상태

- 공식 공개 API가 아닌 Claude 웹 사용량 응답을 읽으므로 서비스 변경에 따라 수정이 필요할 수 있습니다.
- 확장은 Chrome용으로 준비했습니다. 기업에서 개발자 모드 또는 Native Messaging을 제한하면 설치가 불가능할 수 있습니다.
- Fable로 명시된 항목만 표시하며 일반 주간 한도를 Fable로 바꾸어 표시하지 않습니다.
- 실제 사용자의 확장 설치 및 Claude 계정 사용량 수신은 자동 테스트로 확인할 수 없습니다. 설치 후 연결 확인이 필요합니다.

구현 참고: [Chrome Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging), [확장 설치 안내](https://developer.chrome.com/docs/extensions/get-started/tutorial/hello-world), [서비스 워커 수명](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle).
