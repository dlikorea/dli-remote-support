# 서버 및 업데이트 배포

중계 서버는 Node.js 24 이상에서 실행합니다. `server.js`와 `public/`, 배포용 `feed/` 폴더를 같은 프로젝트에 배치합니다.

## HTTPS

고객과 상담원 양쪽에서 접속할 HTTPS 주소를 준비합니다. 인증서와 개인 키를 직접 지정하거나 인증된 HTTPS 역방향 프록시를 구성합니다.

| 환경 변수 | 용도 |
| --- | --- |
| `DLI_BIND` | 바인딩 주소, 기본값 `127.0.0.1` |
| `DLI_PORT` | 수신 포트, 기본값 `8765` |
| `DLI_PUBLIC_ORIGIN` | 고객이 사용하는 HTTPS 원본 주소 |
| `DLI_TLS_CERT` | 직접 HTTPS로 실행할 때 사용할 PEM 인증서 경로 |
| `DLI_TLS_KEY` | 직접 HTTPS로 실행할 때 사용할 PEM 개인 키 경로 |
| `DLI_OPERATOR_KEY` | 상담원 인증에 사용할 12~128자 키 |
| `DLI_OPERATOR_KEY_FILE` | 상담원 키를 읽을 파일, `DLI_OPERATOR_KEY`와 함께 지정할 수 없음 |
| `DLI_PROXY_TOKEN` | 역방향 프록시 전용 64자리 16진수 토큰 |
| `DLI_UPDATE_DIR` | 업데이트 목록과 설치 파일을 제공할 폴더 |

`Initialize-NasRelease.ps1`은 최초 배포에 필요한 상담원 키와 프록시 토큰을 무작위로 생성합니다. 인증서 개인 키, 상담원 키, 프록시 토큰, 업데이트 발행 키는 각 배포 환경에서 관리합니다.

```powershell
.\Initialize-NasRelease.ps1 -Server https://support.example.com/ `
    -Version 1.2.0.0 -KeyPath D:\ReleaseKeys\dli-update.dpapi `
    -ReleaseDir .\dist -CreateKey
```

Nginx Proxy Manager 구성을 생성할 때는 `-NginxProxyManager`를 추가합니다. 생성된 `nas/relay.env`와 프록시의 `X-DLI-Proxy-Token` 값이 일치하도록 구성하고, 원래 `Host`와 `X-Forwarded-Proto`를 전달합니다. 각 배포 환경에 맞는 프록시와 컨테이너 네트워크를 설정합니다.

```sh
docker compose -f compose.yaml up -d
curl --fail http://127.0.0.1:8765/healthz
```

외부 HTTPS 주소의 `/healthz`, `/`, `/download`와 다른 네트워크의 원격 연결을 확인합니다.

## 새 버전

동일한 업데이트 발행 키를 사용하고 네 자리 버전 번호를 올립니다. Windows 게시자 서명을 적용할 때에는 앱과 설치 프로그램의 Authenticode 서명을 먼저 완료합니다.

```powershell
.\Build-Installer.ps1 -Version 1.2.1.0 `
    -Server https://support.example.com/ `
    -KeyPath D:\ReleaseKeys\dli-update.dpapi -OutDir .\dist
.\Publish-Update.ps1 -Setup .\dist\DLI-RemoteSupport-Setup.exe `
    -KeyPath D:\ReleaseKeys\dli-update.dpapi -FeedDir .\feed
```

서버에는 `feed/<버전>/` 폴더를 먼저 올린 다음 `feed/manifest.json`을 교체합니다. `/download`는 현재 목록의 설치 파일로 연결합니다. 업데이트 발행 후 기존 앱에서 새 버전 제안, 서명 검증, 다운로드와 파일 교체를 확인합니다.
