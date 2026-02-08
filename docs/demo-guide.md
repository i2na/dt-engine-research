# DT Engine 데모 가이드

**독립형 BIM 디지털 트윈 엔진 - 실행 가이드**

---

## 목차

1. [시스템 요구사항](#1-시스템-요구사항)
2. [Revit 플러그인 빌드 및 설치](#2-revit-플러그인-빌드-및-설치)
3. [Revit 모델 내보내기](#3-revit-모델-내보내기)
4. [웹 뷰어 실행](#4-웹-뷰어-실행)
5. [주요 기능 사용법](#5-주요-기능-사용법)
6. [문제 해결](#6-문제-해결)

---

## 1. 시스템 요구사항

### Revit 플러그인 (서버/로컬 PC)

- **운영체제**: Windows 10/11 (64-bit)
- **Revit 버전**: Autodesk Revit 2023 또는 2024
- **.NET Framework**: .NET Framework 4.8
- **개발 환경** (빌드 시):
    - Visual Studio 2022 (Community Edition 이상)
    - .NET Desktop Development 워크로드
    - NuGet 패키지 관리자

### 웹 뷰어 (클라이언트)

- **Node.js**: v18.0 이상
- **브라우저**:
    - Chrome 113+ (WebGPU 지원)
    - Edge 113+ (WebGPU 지원)
    - Firefox 141+ (WebGPU 지원)
    - Safari 18+ (제한적 WebGPU, WebGL 2.0 폴백)
- **메모리**: 최소 4GB RAM (권장 8GB 이상)

---

## 2. Revit 플러그인 빌드 및 설치

### 2.1. 소스 코드 확인

프로젝트 폴더의 `revit-plugin/DTExtractor/` 디렉토리에서 다음 파일들을 확인합니다:

```
revit-plugin/DTExtractor/
├── DTExtractor.csproj       # 프로젝트 파일
├── DTExtractor.addin        # Revit 애드인 매니페스트
├── Core/                    # 핵심 로직
│   ├── DTGeometryExporter.cs
│   ├── DTMetadataCollector.cs
│   ├── DTGltfBuilder.cs
│   └── DTParquetWriter.cs
├── Models/                  # 데이터 모델
│   ├── DTElementRecord.cs
│   └── DTParameterRecord.cs
├── Commands/                # Revit 명령
│   └── ExportCommand.cs
└── App.cs                   # 애플리케이션 진입점
```

### 2.2. Visual Studio에서 빌드

1. **Visual Studio 2022 실행**

2. **프로젝트 열기**
    - `File` → `Open` → `Project/Solution`
    - `revit-plugin/DTExtractor/DTExtractor.csproj` 선택

3. **Revit 버전 선택**
    - 상단 도구 모음에서 Configuration 선택:
        - `Debug-R2023` (Revit 2023용)
        - `Debug-R2024` (Revit 2024용)

4. **NuGet 패키지 복원**
    - Solution Explorer에서 프로젝트 우클릭
    - `Restore NuGet Packages` 선택
    - 다음 패키지가 자동 설치됩니다:
        - SharpGLTF.Toolkit (glTF 생성)
        - Parquet.Net (Parquet 파일 생성)
        - Newtonsoft.Json (JSON 직렬화)

5. **빌드 실행**
    - `Build` → `Build Solution` (또는 `Ctrl+Shift+B`)
    - 빌드 성공 시 Output 창에 `Build succeeded` 메시지 표시

6. **빌드 결과 확인**
    - DLL 파일 위치: `revit-plugin/DTExtractor/bin/Debug-R2023/DTExtractor.dll`
    - 빌드 후 자동으로 다음 위치에 복사됩니다:
        ```
        %AppData%\Autodesk\Revit\Addins\2023\DTExtractor.dll
        %AppData%\Autodesk\Revit\Addins\2023\DTExtractor.addin
        ```

### 2.3. 수동 설치 (빌드 후 자동 복사 실패 시)

1. **빌드 결과물 복사**

    ```
    소스: revit-plugin/DTExtractor/bin/Debug-R2023/
    대상: C:\Users\[사용자명]\AppData\Roaming\Autodesk\Revit\Addins\2023\

    복사할 파일:
    - DTExtractor.dll
    - SharpGLTF.Toolkit.dll
    - Parquet.Net.dll
    - Newtonsoft.Json.dll
    ```

2. **애드인 매니페스트 복사**
    ```
    소스: revit-plugin/DTExtractor/DTExtractor.addin
    대상: C:\Users\[사용자명]\AppData\Roaming\Autodesk\Revit\Addins\2023\
    ```

### 2.4. Revit에서 플러그인 확인

1. **Revit 실행**

2. **리본 메뉴 확인**
    - 상단 리본에 `DT Engine` 탭이 추가되었는지 확인
    - 탭 내부에 `Export to DT Engine` 버튼 확인

3. **플러그인 오류 확인**
    - 리본이 표시되지 않으면 Revit 재시작
    - 여전히 표시되지 않으면:
        - Revit 메뉴: `R` (좌측 상단) → `Options` → `General` → `Show startup window`
        - Revit 재시작 후 Startup 창에서 Add-In Manager 열기
        - DTExtractor가 로드되었는지, 에러가 있는지 확인

---

## 3. Revit 모델 내보내기

### 3.1. Revit 프로젝트 준비

1. **3D 뷰 생성**
    - Revit에서 내보낼 모델 열기
    - `View` → `3D View` → `Default 3D View` 생성
    - 내보낼 요소가 모두 표시되는지 확인

2. **불필요한 요소 숨기기** (선택사항)
    - Temporary Hide/Isolate 기능 사용
    - 실시간 렌더링 성능을 위해 불필요한 디테일 숨김 권장

### 3.2. 내보내기 실행

1. **Export 명령 실행**
    - 리본 메뉴: `DT Engine` → `Export to DT Engine` 클릭

2. **출력 경로 선택**
    - 파일 저장 대화상자 표시
    - 파일 이름 입력 (예: `MyBuilding.glb`)
    - **중요**: 웹 뷰어에서 접근할 수 있는 경로 선택:
        ```
        [프로젝트폴더]/web-viewer/public/models/MyBuilding.glb
        ```

3. **내보내기 진행**
    - 진행 상황 대화상자 표시
    - 모델 크기에 따라 1~10분 소요
    - 다음 작업이 순차적으로 실행됩니다:
        1. 3D 뷰 테셀레이션 (Tessellation)
        2. 형상 데이터를 GLB로 직렬화 (Draco 압축)
        3. 7종 파라미터 전수 추출
        4. 메타데이터를 Parquet로 직렬화 (Snappy 압축)
        5. GUID 일치성 검증

4. **내보내기 완료**
    - 완료 대화상자에 다음 정보 표시:

        ```
        Files exported successfully:

        Geometry: MyBuilding.glb (45.2 MB)
        Metadata: MyBuilding.parquet (8.7 MB)

        Output folder: C:\...\web-viewer\public\models\
        ```

### 3.3. 출력 파일 확인

내보내기 완료 후 다음 파일이 생성되어야 합니다:

```
web-viewer/public/models/
├── MyBuilding.glb       # 형상 데이터 (glTF 2.0 Binary)
└── MyBuilding.parquet   # 메타데이터 (Apache Parquet)
```

**파일 내용**:

- **GLB 파일**:
    - 테셀레이션된 메쉬 데이터 (정점, 법선, UV)
    - 머티리얼 및 텍스처
    - Draco 압축 (원본 대비 40~80% 용량)
    - GPU 인스턴싱 데이터 (동일 형상 재사용)
    - GUID 내장 (glTF extras 필드)

- **Parquet 파일**:
    - 각 BIM 요소의 GUID (Primary Key)
    - 7종 파라미터 (Instance, Type, BuiltIn, Shared, Project, Global, Family)
    - 형상 메타데이터 (BoundingBox, Volume, Area)
    - 공간 정보 (Level, Phase)
    - Snappy 압축 (JSON 대비 70~90% 용량)

---

## 4. 웹 뷰어 실행

### 4.1. 의존성 설치

1. **터미널 열기**
    - Windows: PowerShell 또는 Command Prompt
    - macOS/Linux: Terminal

2. **프로젝트 폴더로 이동**

    ```bash
    cd [프로젝트폴더]/web-viewer
    ```

3. **Node.js 버전 확인**

    ```bash
    node --version
    # v18.0.0 이상이어야 함
    ```

4. **NPM 패키지 설치**

    ```bash
    npm install
    ```

    설치되는 주요 패키지:
    - `three`: Three.js 3D 렌더링 엔진
    - `duckdb-wasm`: 브라우저 내 SQL 쿼리 엔진
    - `apache-arrow`: 컬럼 기반 데이터 처리
    - `vite`: 개발 서버 및 빌드 도구
    - `typescript`: TypeScript 컴파일러

### 4.2. 모델 파일 배치

1. **파일 위치 확인**

    ```
    web-viewer/public/models/
    ├── sample.glb       ← 내보낸 GLB 파일을 이 이름으로 변경 또는
    └── sample.parquet   ← 내보낸 Parquet 파일을 이 이름으로 변경
    ```

2. **또는 코드 수정**
    - `web-viewer/src/main.ts` 파일 열기
    - 44~45번째 줄 수정:
        ```typescript
        const glbUrl = "/models/MyBuilding.glb"; // 실제 파일명으로 변경
        const parquetUrl = "/models/MyBuilding.parquet";
        ```

### 4.3. 개발 서버 시작

1. **개발 모드 실행**

    ```bash
    npm run dev
    ```

2. **서버 시작 확인**
    - 터미널에 다음 메시지 표시:

        ```
        VITE v5.1.4  ready in 324 ms

        ➜  Local:   http://localhost:3000/
        ➜  Network: http://192.168.0.10:3000/
        ```

3. **브라우저 자동 실행**
    - 기본 브라우저가 자동으로 열림
    - 수동 접속: `http://localhost:3000`

### 4.4. 웹 뷰어 로딩 과정

브라우저에서 다음 순서로 초기화가 진행됩니다:

1. **렌더링 백엔드 감지**
    - WebGPU 사용 가능 여부 확인
    - 폴백: WebGL 2.0

2. **모델 로딩**
    - GLB 파일 다운로드 및 파싱
    - GUID 메타데이터 추출 (L1 캐시 채우기)
    - 3D 씬 구성

3. **Parquet 초기화**
    - DuckDB-WASM 엔진 로드
    - Parquet 파일 URL 등록 (실제 다운로드는 쿼리 시)

4. **렌더링 시작**
    - 모델이 화면에 표시됨
    - 오비트 컨트롤 활성화

**로딩 실패 시**:

- 화면 중앙에 에러 메시지 표시
- F12 개발자 도구의 Console 탭에서 상세 로그 확인

---

## 5. 주요 기능 사용법

### 5.1. 카메라 조작

| 입력                   | 동작         |
| ---------------------- | ------------ |
| 마우스 좌클릭 + 드래그 | 회전 (Orbit) |
| 마우스 우클릭 + 드래그 | 이동 (Pan)   |
| 마우스 휠              | 줌 인/아웃   |
| 휠 클릭 + 드래그       | 이동 (Pan)   |

**모바일/터치**:

- 1-finger 드래그: 회전
- 2-finger 드래그: 이동
- 핀치: 줌

### 5.2. 렌더링 모드 (좌측 사이드바)

**Material 모드** (기본):

- PBR (Physically Based Rendering) 재질
- 광원 및 그림자 적용
- 사실적 시각화

**Wireframe 모드**:

- 메쉬의 삼각형 구조 표시
- 형상 토폴로지 확인
- 모델 품질 검증

**X-Ray 모드**:

- 반투명 렌더링 (opacity 0.3)
- 내부 구조 투시
- 숨겨진 요소 확인

### 5.3. 조명 제어

**Ambient Intensity (환경광 강도)**:

- 범위: 0.0 ~ 2.0
- 기본값: 0.6
- 전체적인 밝기 조절
- 그림자 없음

**Directional Intensity (직사광 강도)**:

- 범위: 0.0 ~ 3.0
- 기본값: 1.5
- 방향성 있는 주 광원
- 그림자 생성

**Enable Shadows (그림자 활성화)**:

- 체크박스 활성화 시 실시간 그림자 렌더링
- 성능에 영향을 줄 수 있음 (대형 모델)

### 5.4. Click-to-Data: BIM 요소 정보 조회

**핵심 기능**: 3D 모델에서 객체를 클릭하면 Revit 파라미터가 우측 패널에 즉시 표시됩니다.

**사용 방법**:

1. 3D 뷰에서 원하는 BIM 요소를 **좌클릭**
2. 선택된 객체가 파란색으로 하이라이트
3. 우측 사이드바에 메타데이터 표시:
    - **기본 정보**: GUID, Element ID, Family, Type, Level, Phase
    - **수량 정보**: Volume, Area
    - **Instance Parameters**: 인스턴스별 고유 값
    - **Type Parameters**: 패밀리 타입 공유 값
    - **Performance**: 쿼리 레이턴시 (L1/L2/L3 캐시 소스 표시)

**4단 캐시 시스템**:

- **L1 (0ms)**: glTF 내장 기본 정보 (즉시 표시)
- **L2 (<5ms)**: IndexedDB 로컬 캐시 (최근 조회 객체)
- **L3 (<100ms)**: DuckDB-WASM Parquet 쿼리 (서버 없이 브라우저 내)
- **L4 (<300ms)**: PostgreSQL 서버 API (미구현, 향후 확장)

**Console 로그 확인**:

```
F12 → Console 탭:
Clicked element: e1a2b3c4-5678-90ab-cdef-1234567890ab
Metadata retrieved from L3 in 87.3ms
```

### 5.5. 뷰 제어

**Reset View (뷰 초기화)**:

- 카메라를 초기 위치로 리셋
- 방향: (50, 50, 50)에서 원점 바라봄

**Fit All (전체 맞춤)**:

- 모델 전체가 화면에 맞도록 자동 줌
- 카메라를 모델 중심으로 재배치

**Show Grid (그리드 표시)**:

- 바닥에 100x100 그리드 표시
- 단위: feet (Revit 내부 단위)

**Show Axes (축 표시)**:

- X (빨강), Y (초록), Z (파랑) 축 표시
- 길이: 20 units

### 5.6. 성능 모니터링

화면 상단 중앙의 **Stats 패널**에 실시간 정보 표시:

```
Backend: WEBGL2 | Triangles: 1,234,567 | Draw Calls: 42 | Cache: 128
```

- **Backend**: 현재 렌더링 백엔드 (WEBGPU / WEBGL2)
- **Triangles**: 렌더링되는 삼각형 수
- **Draw Calls**: GPU draw call 수 (낮을수록 최적화됨)
- **Cache**: L1 캐시에 저장된 요소 수

---

## 6. 문제 해결

### 6.1. Revit 플러그인 문제

**문제**: 리본에 `DT Engine` 탭이 표시되지 않음

**해결**:

1. Revit 재시작
2. 애드인 파일 경로 확인:
    ```
    C:\Users\[사용자명]\AppData\Roaming\Autodesk\Revit\Addins\2023\
    → DTExtractor.dll
    → DTExtractor.addin
    ```
3. `.addin` 파일을 메모장으로 열어 DLL 경로가 올바른지 확인
4. Revit 버전과 빌드 Configuration이 일치하는지 확인 (2023 vs 2024)

**문제**: 내보내기 중 오류 발생

**해결**:

1. 3D 뷰가 존재하는지 확인
2. 출력 경로에 쓰기 권한이 있는지 확인
3. Revit Console에서 상세 에러 로그 확인
4. 모델이 너무 크면 (10GB+) 메모리 부족 가능 → 불필요한 요소 숨기기

### 6.2. 웹 뷰어 문제

**문제**: "모델 로드 실패" 메시지

**해결**:

1. 파일 경로 확인:
    ```bash
    ls web-viewer/public/models/
    # sample.glb 및 sample.parquet 존재 확인
    ```
2. 파일명이 코드와 일치하는지 확인 (`src/main.ts` 44~45줄)
3. 브라우저 Console (F12)에서 상세 에러 확인:
    - 404 Not Found: 파일 경로 문제
    - CORS Error: 로컬 서버 사용 필수 (`npm run dev`)

**문제**: DuckDB-WASM 초기화 실패

**해결**:

1. 브라우저가 SharedArrayBuffer를 지원하는지 확인 (Chrome 92+)
2. 브라우저 플래그 활성화 필요 (오래된 브라우저):
    ```
    chrome://flags/#enable-webassembly-threads
    ```
3. HTTPS 또는 localhost 환경에서만 작동 (보안 정책)

**문제**: 클릭해도 메타데이터가 표시되지 않음

**해결**:

1. F12 Console에서 "Clicked element" 로그 확인
    - 로그 없음: Raycasting 실패 (모델이 로드되지 않음)
    - GUID 로그 있음: Parquet 파일 문제
2. Parquet 파일 내 GUID와 GLB 내 GUID 일치 확인:
    - 동일한 Revit 모델에서 동시에 내보낸 파일인지 확인
3. 네트워크 탭에서 Parquet HTTP 요청 확인

**문제**: 렌더링 성능 저하

**해결**:

1. Stats 패널에서 삼각형 수 확인
    - 100만 개 이상: LOD 최적화 필요
2. Draw Calls가 높으면 (1000+):
    - 재질 통합 부족 (Revit 내보내기 품질 문제)
3. 그림자 비활성화 (`Enable Shadows` 체크 해제)
4. X-Ray 모드 대신 Material 모드 사용

### 6.3. 브라우저 호환성

| 브라우저      | WebGPU | WebGL 2.0 | 권장 여부       |
| ------------- | ------ | --------- | --------------- |
| Chrome 113+   | ✓      | ✓         | ✓ 최우선        |
| Edge 113+     | ✓      | ✓         | ✓ 권장          |
| Firefox 141+  | ✓      | ✓         | ✓ 권장          |
| Safari 18+    | 제한적 | ✓         | △ WebGL 모드    |
| 구형 브라우저 | ✗      | ✓         | △ 업데이트 권장 |

**WebGPU 미지원 환경**:

- 자동으로 WebGL 2.0 폴백
- 성능 차이: 약 20~30% (여전히 실용적)
- Stats 패널에서 "Backend: WEBGL2" 표시

---

## 7. 다음 단계

### 7.1. 프로덕션 배포

**웹 뷰어 빌드**:

```bash
cd web-viewer
npm run build
```

빌드 결과: `web-viewer/dist/` 폴더

- 정적 파일 생성 (HTML, JS, CSS)
- 웹 서버 (Apache, Nginx, IIS)에 배포 가능

### 7.2. 확장 기능 개발

현재 PoC는 핵심 기능만 구현되어 있습니다. 청사진 문서를 참조하여 다음 기능을 추가할 수 있습니다:

- **IoT 통합**: WebSocket 기반 실시간 센서 데이터 오버레이
- **측정 도구**: 거리, 면적, 체적, 각도 측정 플러그인
- **공간 분석**: Room 면적, 재실률, A\* 기반 피난 경로 분석
- **버전 관리**: GUID 기반 모델 diff 및 변경 시각화
- **충돌 감지**: BVH + GJK 기반 물리적 충돌 감지

### 7.3. 기술 지원

문제 발생 시:

1. F12 개발자 도구의 Console 탭에서 에러 로그 확인
2. `docs/dt-engine-research.md` 청사진 문서의 해당 섹션 참조
3. GitHub Issues 또는 내부 기술 지원팀 문의

---

**문서 버전**: 1.0.0
**최종 수정**: 2026-02-08
**작성자**: DT Engine Development Team
