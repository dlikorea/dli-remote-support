# Code signing policy

## 진행 상태

2026년 11월 19일 접수한 SignPath Foundation 무료 오픈소스 코드 서명 신청은 프로젝트의 공개 사용·기여 이력과 외부 인지도가 부족하다는 사유로 승인되지 않았습니다. 재단은 공개 활동과 커뮤니티 이용 이력이 쌓인 뒤 재신청할 수 있다고 안내했습니다.

2026-11-20 고객 고정 다운로드를 Microsoft Store의 웹 설치 파일로 전환했습니다. 실제 다운로드 EXE의 Authenticode 상태는 `Valid`, 서명자는 `Microsoft Corporation`입니다. 설치된 1.3.1.0 MSIX의 SignatureKind는 `Store`로 확인했습니다. 기존 1.2.0.0 EXE는 별도 서명을 받지 않았으며, 기존 설치 사용자의 업데이트 파일로만 유지합니다.

[SignPath Foundation 신청 조건](https://signpath.org/terms)

Microsoft Store ID는 `9P0HBM1W0KKZ`이며, 게시자 표시 이름 정정 후 제출한 1.3.1.0 x64 MSIX가 게시됐습니다. GitHub의 점검 패키지와 제출용 패키지는 unsigned 산출물입니다. Microsoft 서명은 Store를 통한 고객 배포에 적용되며, 기존 EXE를 회사 명의의 인증서로 서명한 것은 아닙니다. [Store 배포 안내](store/README.md)

## 담당자

| 역할 | 담당 |
| --- | --- |
| Authors / maintainers | GitHub 저장소 소유자 `dlikorea` |
| Reviewers | GitHub 저장소 소유자 `dlikorea` |
| Signing approvers | GitHub 저장소 소유자 `dlikorea` |

외부 기여는 유지관리자의 검토를 거쳐 반영합니다. 유지관리자가 공개 소스에서 제출용 패키지를 빌드하고 Microsoft Store에 제출하며, Microsoft의 심사와 서명을 거쳐 배포합니다. 현재 SignPath Foundation의 운영 서명은 사용하지 않습니다.

[담당 계정](https://github.com/dlikorea)

## 빌드와 배포

GitHub Actions의 Windows 호스팅 실행기에서 공개 커밋을 빌드합니다. 빌드에는 업데이트 검증용 공개 키만 사용합니다. 워크플로는 검토할 unsigned EXE와 Store 제출용 MSIX를 생성합니다. 운영 Store 버전은 MSIX를 Microsoft에 제출하고 심사·서명·게시를 마친 뒤 배포합니다.

기존 EXE 설치 프로그램은 고객 앱을 리소스로 포함합니다. 이 배포 경로에 추후 외부 Authenticode 서명을 적용할 경우에는 안쪽 고객 앱을 먼저 서명한 후 설치 프로그램에 포함하고, 바깥 설치 프로그램을 서명해야 합니다. 최종 파일을 검증한 다음 별도로 관리하는 업데이트 발행 키로 업데이트 목록을 발행합니다. Store 버전은 이 EXE 업데이트 경로를 사용하지 않습니다.

고객은 Windows에서 설치 파일의 디지털 서명을 확인할 수 있습니다. 서명 상태를 확인할 때에는 `Get-AuthenticodeSignature`를 사용할 수 있습니다. 새 파일의 SmartScreen 평판은 Windows의 별도 평가를 받습니다.

## Privacy policy

원격지원 중 화면, 입력, 메모, 요청된 파일 탐색 결과와 파일 내용은 설정된 중계 서버를 통해 전달됩니다. Store 버전의 설치와 업데이트는 Microsoft Store에서 관리하며, 기존 EXE 버전은 실행 시 NAS의 업데이트 목록을 확인합니다. 구체적인 처리 방식과 보관 범위는 Privacy policy에 설명합니다.

[Privacy policy](PRIVACY.md) · [재단 신청 조건](https://signpath.org/terms)
