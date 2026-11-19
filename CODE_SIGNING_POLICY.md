# Code signing policy

## 진행 상태

2026년 11월 19일 접수한 SignPath Foundation 무료 오픈소스 코드 서명 신청은 프로젝트의 공개 사용·기여 이력과 외부 인지도가 부족하다는 사유로 승인되지 않았습니다. 재단은 공개 활동과 커뮤니티 이용 이력이 쌓인 뒤 재신청할 수 있다고 안내했습니다.

현재 공개 다운로드의 1.2.0.0 설치 파일은 Windows Authenticode 서명이 없습니다. 실제 서명 적용과 배포에는 사용할 수 있는 공개 신뢰 코드 서명 인증서 또는 서명 서비스가 필요합니다.

[SignPath Foundation 신청 조건](https://signpath.org/terms)

Microsoft Store에 MSIX로 제출해 Microsoft의 무료 패키지 서명을 사용하는 별도 배포 경로를 준비하고 있습니다. 현재 검증 빌드는 로컬 점검용 ID를 사용한 unsigned MSIX입니다. Store 승인과 재서명을 완료하기 전에는 Windows 신뢰 서명 적용이나 새 웹 설치 배포가 완료됐다고 안내하지 않습니다. [Store 배포 안내](store/README.md)

## 담당자

| 역할 | 담당 |
| --- | --- |
| Authors / maintainers | GitHub 저장소 소유자 `dlikorea` |
| Reviewers | GitHub 저장소 소유자 `dlikorea` |
| Signing approvers | GitHub 저장소 소유자 `dlikorea` |

외부 기여는 유지관리자의 검토를 거쳐 반영합니다. SignPath Foundation의 운영 서명은 승인된 공개 소스에서 생성한 빌드에 한해 요청하며, 릴리스별 서명 승인은 담당자가 수행합니다. GitHub와 SignPath 계정의 다중 인증은 재단의 요구 조건을 따릅니다.

[담당 계정](https://github.com/dlikorea)

## 빌드와 배포

GitHub Actions의 Windows 호스팅 실행기에서 공개 커밋을 빌드합니다. 빌드에는 업데이트 검증용 공개 키만 사용합니다. 현재 워크플로는 검토할 unsigned 산출물을 생성합니다. 코드 서명 서비스가 준비되면 빌드 산출물의 출처를 확인하는 서명 절차를 연동합니다.

현재 설치 프로그램은 고객 앱을 리소스로 포함합니다. 운영 서명 단계에서는 안쪽 고객 앱을 먼저 서명한 후 설치 프로그램에 포함하고, 바깥 설치 프로그램을 서명합니다. 최종 설치 파일을 검증한 다음 별도로 관리하는 업데이트 발행 키로 업데이트 목록을 발행합니다.

고객은 Windows에서 설치 파일의 디지털 서명을 확인할 수 있습니다. 서명 상태를 확인할 때에는 `Get-AuthenticodeSignature`를 사용할 수 있습니다. 새 파일의 SmartScreen 평판은 Windows의 별도 평가를 받습니다.

## Privacy policy

원격지원 중 화면, 입력, 메모, 요청된 파일 탐색 결과와 파일 내용은 설정된 중계 서버를 통해 전달됩니다. 설치된 앱은 실행 시 업데이트를 확인합니다. 구체적인 처리 방식과 보관 범위는 Privacy policy에 설명합니다.

[Privacy policy](PRIVACY.md) · [재단 신청 조건](https://signpath.org/terms)
