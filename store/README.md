# Microsoft Store 웹 설치 배포

홈페이지의 다운로드 링크에서 Microsoft가 생성하는 작은 EXE 설치 파일을 받아, 디엘아이 원격지원 MSIX를 설치하는 경로입니다. 고객이 두 번 설치하는 절차는 없습니다. 일반적인 지원 환경에서는 Store 앱을 열지 않고 설치됩니다. 시스템·지역 등 조건에 맞지 않으면 Store 앱으로 전환될 수 있습니다.

## 준비 상태

- `Build-Msix.ps1`은 기존 고객 앱과 파일·모니터 기능을 포함하는 x64 MSIX를 생성합니다.
- Store 빌드는 NAS 설치파일 업데이터와 설치/제거 실행 파일을 포함하지 않습니다. 설치·제거·패키지 업데이트는 Windows와 Microsoft Store가 관리합니다.
- 고객 화면에는 역할 선택을 추가하지 않습니다. 상담원은 기존 HTTPS 웹 화면과 별도 상담원 인증을 사용합니다.
- **현재 운영 다운로드는 아직 1.2.0.0 EXE입니다. Store 등록, 심사, 서명 또는 웹 설치 배포 완료를 뜻하지 않습니다.**

## 빌드

Windows .NET Framework 컴파일러와 Windows SDK의 MakeAppx가 필요합니다. SDK를 설치하거나 Microsoft의 `Microsoft.Windows.SDK.BuildTools` NuGet 패키지에서 도구를 사용할 수 있습니다.

등록 전 점검용 패키지는 실제 Store ID를 사용하지 않습니다.

```powershell
.\Build-Msix.ps1 -Version 1.3.0.0 -Server https://remote.dlicompany.com/ `
  -MakeAppx 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe' `
  -OutDir .\dist\msix-validation -ValidationBuild
.\store\Test-Msix.ps1 -Package .\dist\msix-validation\DLI-RemoteSupport-1.3.0.0-validation-x64.msix
```

점검용 패키지는 고객 배포·Store 제출 대상이 아니며 서명돼 있지 않습니다. 테스트를 위해 고객 PC의 인증서 저장소나 SmartScreen 설정을 변경하지 않습니다. 점검 스크립트는 패키지 구조와 압축 해제한 앱의 기능을 검사합니다. 실제 Store 설치·자동 업데이트와 두 PC 간 원격조작은 정식 패키지로 추가 확인해야 합니다.

## 실제 제출 패키지

1. Partner Center에 디엘아이 회사 계정을 등록하고 회사 정보를 확인합니다. 필수 약관 동의와 신원 확인은 담당자의 확인이 필요할 수 있습니다.
2. MSIX 제품으로 **디엘아이 원격지원** 이름을 예약합니다. EXE/MSI 제품 경로는 무료 재서명을 제공하지 않습니다.
3. 제품 ID 페이지의 Package/Identity/Name, Package/Identity/Publisher, Package/Properties/PublisherDisplayName 및 Store ID를 `identity.example.json` 형식으로 복사합니다. 이 값은 비밀 키가 아닙니다.
4. `Build-Msix.ps1 -IdentityPath .\store\identity.json`으로 다시 빌드합니다. 정식 ID 없이 제출용 패키지를 생성하지 않습니다.
5. 설명, 스크린샷, 개인정보 정책, 연령 등급, 테스트 안내와 함께 MSIX를 제출합니다. `runFullTrust`는 화면 캡처·현재 사용자 권한의 입력·파일 전송에 필요한 데스크톱 실행 권한으로 설명합니다. 관리자 권한 상승이나 무인 접속은 제공하지 않습니다.
6. 승인 후 Store 설치로 원격지원·파일 왕복 전송·접근 가능한 다른 드라이브·지원 종료·모니터 전환을 검증합니다. 업데이트를 제출하고 설치된 Store 앱의 갱신을 확인합니다.
7. Store 공식 badge 생성기의 Direct 모드에서 웹 설치 링크를 얻습니다. 승인 전에 URL을 추측하거나 고객 다운로드를 새 파일로 교체하지 않습니다.
8. 검증 후 NAS의 기존 `/download`를 해당 웹 설치 경로로 연결합니다. 기존 EXE 설치 사용자의 `/updates/manifest.json` 경로는 Store 패키지로 바꾸지 않습니다. 기존 EXE에서 Store 버전으로의 전환은 별도 설치·중복 방지 확인이 필요합니다.

## 심사용 설명 초안

- 제품명: 디엘아이 원격지원
- 요약: 디엘아이 상담원과 화면을 공유하고 파일을 주고받는 원격지원 앱입니다.
- 대상: 디엘아이 상담원과 통화 중인 고객. 상담원은 별도의 브라우저 화면에서 인증합니다.
- 동의: 고객이 앱에서 일회용 코드를 만들고 전달해야 연결됩니다. 연결 안내에 화면 조작과 파일 접근 범위를 명시하며, 고객은 지원 종료 버튼으로 연결을 끊습니다.
- 기능: 원격 마우스·키보드, 모니터 선택, 상담원 메모, 접근 가능한 드라이브의 파일 탐색·가져오기·전송. 파일당 20 MiB 제한.
- 권한: 현재 로그인한 Windows 사용자 권한으로만 실행합니다. 관리자 보안 화면을 조작하지 않습니다. 항상 실행되는 서비스나 자동 시작 항목을 설치하지 않습니다.
- 지원 연락처: info@dlicompany.com
- 개인정보 정책: https://github.com/dlikorea/dli-remote-support/blob/main/PRIVACY.md

심사에 필요한 상담원 테스트 자격 증명은 공개 저장소에 넣지 않습니다. 제출 전 운영용 상담원 키와 분리된 테스트 접근 방법을 마련하고 Partner Center의 비공개 심사 안내에만 기재해야 합니다.

## 공식 안내

- [Store Web Installer](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-use-store-web-installer-for-distribution)
- [회사 등록 무료화](https://blogs.windows.com/windowsdeveloper/2026/05/07/publish-to-microsoft-store-as-a-company-now-with-free-registration-and-faster-onboarding/)
- [MSIX 수동 패키징](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion)
- [서명 옵션과 Store MSIX 재서명](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
