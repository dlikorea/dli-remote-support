# 디엘아이 원격지원

DLI Remote Support는 Windows 고객 앱과 브라우저 상담원 화면으로 구성된 원격지원 프로그램입니다. 소스 코드는 MIT 라이선스로 공개합니다.

고객이 앱에서 만든 일회용 8자리 코드를 상담원에게 알려주면 화면 공유와 마우스·키보드 조작이 시작됩니다. 상담원은 별도로 관리되는 상담원 키로 인증합니다. 고객은 앱의 **지원 종료** 버튼으로 연결을 종료할 수 있습니다.

## 다운로드

고객과 상담원은 같은 Windows 설치 파일을 사용합니다. 설치된 앱은 실행할 때 업데이트를 확인합니다.

[디엘아이 원격지원 설치 파일](https://remote.dlicompany.com/download)

SignPath Foundation 무료 Authenticode 서명 신청은 프로젝트의 공개 사용·기여 이력이 부족하다는 사유로 승인되지 않았습니다. 현재 1.2.0.0 배포 파일에는 Windows 게시자 서명이 없으며, SmartScreen 경고가 표시될 수 있습니다. 서명 상태는 Code signing policy에서 안내합니다.

[Code signing policy](CODE_SIGNING_POLICY.md) · [Privacy policy](PRIVACY.md) · [MIT License](LICENSE)

Microsoft Store의 무료 MSIX 서명과 웹 설치 파일을 사용하는 배포 경로로 1.3.0.0 패키지를 제출했습니다. 현재 심사 중이며 위 다운로드는 기존 EXE입니다. 승인·서명과 새 다운로드 배포는 아직 완료되지 않았습니다. 등록 정보와 배포 절차는 [Store 배포 안내](store/README.md)를 참고하세요.

## 사용

1. Windows 10/11 및 .NET Framework 4.8 이상인 PC에 설치합니다.
2. 고객 앱의 **지원 코드 만들기**를 누르고 통화 중인 상담원에게 코드를 알려줍니다.
3. 상담원은 브라우저에서 배포 서버 주소를 열고 고객 코드와 상담원 키를 입력합니다.
4. 상담원은 고객 화면에서 마우스와 키보드를 조작합니다. F8 키로 브라우저의 원격 키보드 입력을 멈춥니다.
5. 고객과 상담원은 각각 **지원 종료**로 연결을 끝낼 수 있습니다.

상담원 PC에서 다음 명령으로 같은 설치 파일을 실행하면 상담원 바로가기를 추가할 수 있습니다.

```powershell
& '.\DLI-RemoteSupport-Setup.exe' --operator
```

지원 중에는 고객 모니터 선택, 전체 화면 보기, 상담원 메모 전송, 파일 전송을 사용할 수 있습니다. 모니터가 두 대 이상이면 모니터 선택 메뉴가 나타납니다. 상담원은 고객 Windows 계정에서 접근 가능한 드라이브의 파일을 찾아 가져올 수 있고, 수정한 파일을 고객에게 보낼 수 있습니다. 파일당 최대 크기는 20 MiB입니다.

## 소스에서 빌드

Windows의 .NET Framework 컴파일러와 PowerShell을 사용합니다. 고객 앱과 설치 프로그램은 Windows 기본 라이브러리를 참조합니다.

```powershell
.\Build-Installer.ps1 -Version 1.2.0.0 `
    -Server https://remote.dlicompany.com/ `
    -UpdatePublicKeyPath .\release\update-public-key.json `
    -OutDir .\dist
```

이 명령은 공개된 업데이트 검증 키를 내장한 설치 파일을 만듭니다. GitHub Actions도 같은 명령으로 빌드해 커밋과 빌드 산출물의 출처를 기록합니다. 공개 키는 이미 배포된 앱에 포함된 RSA 공개 키입니다.

자체 서버 배포를 위한 업데이트 서명 키는 빌드 담당 Windows 계정에서 생성합니다. 이후 릴리스에도 같은 키를 사용합니다.

```powershell
.\Build-Installer.ps1 -Version 1.2.0.0 `
    -Server https://support.example.com/ `
    -KeyPath D:\ReleaseKeys\dli-update.dpapi `
    -CreateKey -OutDir .\dist
```

새 설치 파일에 Authenticode 서명을 적용할 경우 먼저 Windows 서명을 완료한 후 `Publish-Update.ps1`로 업데이트 목록을 발행합니다. 기존 설치는 업데이트 서명과 파일 해시를 확인합니다. 배포 절차는 DEPLOYMENT.md를 참고하세요.

[서버 및 업데이트 배포](DEPLOYMENT.md)

## 로컬 실행

중계 서버에는 Node.js 24 이상이 필요합니다.

```powershell
node .\server.js
.\Build-Customer.ps1
.\DLI-Customer.exe --server http://127.0.0.1:8765/
```

브라우저에서 `http://127.0.0.1:8765/`를 열고 서버 콘솔에 출력된 로컬 상담원 키를 사용합니다. 외부 고객 연결은 HTTPS 배포를 사용합니다.

## 구성

| 경로 | 역할 |
| --- | --- |
| `customer/` | Windows 화면 캡처, 입력 처리, 고객 창, 파일 접근 |
| `desktop/` | 설치, 제거, 업데이트 검증, 앱 실행 |
| `public/` | 브라우저 상담원 화면 |
| `server.js` | HTTPS 환경에서 사용하는 세션·화면·입력·파일 중계 |
| `assets/` | 앱에 포함되는 디엘아이 로고와 아이콘 |
| `release/update-public-key.json` | 배포 앱에 포함된 업데이트 공개 검증 키 |

화면과 파일은 세션 중 중계 서버의 메모리를 거칩니다. 고객 앱은 현재 Windows 사용자 권한으로 실행되며, 관리자 권한 창과 Windows 보안 화면에는 Windows의 입력 제한이 적용됩니다. 데이터 처리 방식은 Privacy policy에서 확인할 수 있습니다.

[Privacy policy](PRIVACY.md)
