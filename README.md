# DT Engine

## 목차

1. [시스템 요구사항](#1-시스템-요구사항)
2. [Revit 플러그인 빌드 및 설치](#2-revit-플러그인-빌드-및-설치)
3. [Revit 모델 내보내기](#3-revit-모델-내보내기)
4. [웹 뷰어 실행](#4-웹-뷰어-실행)
5. [주요 기능](#5-주요-기능)
6. [문제 해결](#6-문제-해결)

<img width="3840" height="2160" alt="Image" src="https://github.com/user-attachments/assets/20997fc9-a31b-4424-8c5f-3856f280617f" />

---

## 1. 시스템 요구사항

### Revit 플러그인

- **Windows 10/11** (64-bit)
- **Autodesk Revit 2024**
- **.NET Framework 4.8**
- **Visual Studio 2022** (빌드 시)

### 웹 뷰어

- **Node.js v18+**
- **브라우저**: Chrome 113+ / Edge 113+ / Firefox 141+ (WebGPU 지원)
- **메모리**: 최소 4GB (권장 8GB)

---

## 2. Revit 플러그인 빌드 및 설치

### 2.1. Visual Studio 2022에서 빌드

1. **프로젝트 열기**
    - Visual Studio 2022 실행
    - `File` → `Open` → `Project/Solution`
    - `revit-plugin/DTExtractor/DTExtractor.csproj` 선택

2. **빌드 구성 선택**
    - 상단 Configuration 드롭다운에서 선택:
        - `Debug-R2024` (개발용)
        - `Release-R2024` (배포용)

3. **빌드 실행**
    - `Build` → `Build Solution` (또는 `Ctrl+Shift+B`)
    - 빌드 완료 시 자동으로 Revit Addins 폴더에 배포됩니다:
        ```
        C:\Users\[사용자명]\AppData\Roaming\Autodesk\Revit\Addins\2024\
        ```

### 2.2. Revit에서 확인

1. **Revit 2024 실행**
2. **리본 메뉴 확인**
    - 상단 리본에 `DT Engine` 탭 표시 확인
    - `Export to DT Engine` 버튼 확인

3. **플러그인 미표시 시**
    - Revit 재시작
    - 여전히 안 보이면 Addins 폴더에 파일 존재 확인:
        - `DTExtractor.dll`
        - `DTExtractor.addin`
        - 의존 DLL들 (`SharpGLTF.Toolkit.dll`, `Parquet.Net.dll` 등)

---

## 3. Revit 모델 내보내기

### 3.1. 준비

1. **Revit에서 모델 열기**
2. **3D 뷰 생성**
    - `View` → `3D View` → `Default 3D View`
    - 내보낼 요소가 모두 표시되는지 확인

### 3.2. 내보내기 실행

1. **Export 버튼 클릭**
    - 리본 메뉴: `DT Engine` → `Export to DT Engine`

2. **출력 경로 선택**
    - 파일명 입력: **`model.glb`**
    - **중요**: 웹 뷰어의 models 폴더에 저장:
        ```
        [프로젝트폴더]/web-viewer/public/models/model.glb
        ```

3. **내보내기 완료**
    - 완료 대화상자에서 파일 크기 확인
    - 두 파일이 생성됩니다:
        - `model.glb` (형상 데이터)
        - `model.parquet` (메타데이터)

---

## 4. 웹 뷰어 실행

### 4.1. 의존성 설치

```bash
cd web-viewer
npm install
```

### 4.2. 모델 파일 배치

Revit에서 내보낼 때 파일명을 **`model.glb`**로 저장하면 웹 뷰어가 자동으로 `model.glb` / `model.parquet`를 로드합니다.

```
web-viewer/public/models/
├── model.glb
└── model.parquet
```

### 4.3. 개발 서버 시작

```bash
npm run dev
```

브라우저가 자동으로 열리며 `http://localhost:3000`에 접속됩니다.

---

## 5. 주요 기능

### 5.1. 카메라 조작

| 입력                   | 동작       |
| ---------------------- | ---------- |
| 마우스 좌클릭 + 드래그 | 회전       |
| 마우스 우클릭 + 드래그 | 이동       |
| 마우스 휠              | 줌 인/아웃 |

### 5.2. 렌더링 모드 (좌측 사이드바)

- **Material**: PBR 재질, 광원 및 그림자
- **Wireframe**: 메쉬 삼각형 구조 표시
- **X-Ray**: 반투명 렌더링으로 내부 구조 투시

### 5.3. Click-to-Data (객체 정보 조회)

1. 3D 모델에서 **객체 좌클릭**
2. 선택된 객체가 파란색으로 하이라이트
3. **우측 패널에 Revit 파라미터 즉시 표시**:
    - 기본 정보 (GUID, Family, Type, Level)
    - Instance/Type Parameters
    - 수량 정보 (Volume, Area)

**4단 캐시 시스템**:

- L1 (0ms): GLB 내장 정보
- L2 (<5ms): IndexedDB 캐시
- L3 (<100ms): DuckDB-WASM Parquet 쿼리
- L4: PostgreSQL API (미구현)

### 5.4. 뷰 제어

- **Reset View**: 카메라 초기 위치로 리셋
- **Fit All**: 모델 전체 화면 맞춤
- **Show Grid**: 바닥 그리드 표시
- **Show Axes**: XYZ 축 표시

### 5.5. 조명 제어

- **Ambient Intensity**: 환경광 강도 (0.0~2.0)
- **Directional Intensity**: 직사광 강도 (0.0~3.0)
- **Enable Shadows**: 실시간 그림자 렌더링

### 5.6. 성능 모니터링

화면 상단 Stats 패널에 실시간 정보 표시:

```
Backend: WEBGL2 | Triangles: 1,234,567 | Draw Calls: 42 | Cache: 128
```

---

## 6. 문제 해결

### 6.1. Revit 플러그인 미표시

**해결**:

1. Revit 재시작
2. Addins 폴더 확인:
    ```
    C:\Users\[사용자명]\AppData\Roaming\Autodesk\Revit\Addins\2024\
    ```
3. 폴더 내 `DTExtractor.dll`, `DTExtractor.addin` 및 의존 DLL 존재 확인
4. Visual Studio에서 `Debug-R2024` 구성으로 재빌드

### 6.2. 내보내기 오류

**해결**:

1. 3D 뷰가 존재하는지 확인
2. 출력 경로에 쓰기 권한 확인
3. 대형 모델(10GB+)의 경우 불필요한 요소 숨기기

### 6.3. 웹 뷰어 모델 로드 실패

**해결**:

1. 파일 경로 확인:
    ```bash
    ls web-viewer/public/models/
    # model.glb 및 model.parquet 존재 확인
    ```
2. 파일명이 `model.glb`, `model.parquet`인지 확인
3. F12 → Console에서 에러 로그 확인

### 6.4. Click-to-Data 작동 안 함

**해결**:

1. F12 Console에서 "Clicked element" 로그 확인
2. GLB와 Parquet 파일이 동일한 Revit 모델에서 동시에 내보낸 파일인지 확인
3. 브라우저 Network 탭에서 Parquet 파일 로드 확인

### 6.5. 렌더링 성능 저하

**해결**:

1. Stats 패널에서 삼각형 수 확인 (100만 개 이상 시 LOD 최적화 필요)
2. 그림자 비활성화 (`Enable Shadows` 체크 해제)
3. X-Ray 모드 대신 Material 모드 사용

---

## 7. 추가 정보

### 프로덕션 배포

```bash
cd web-viewer
npm run build
```

빌드 결과물(`web-viewer/dist/`)을 웹 서버(Apache, Nginx, IIS)에 배포 가능

### 브라우저 호환성

| 브라우저     | WebGPU | WebGL 2.0 | 권장     |
| ------------ | ------ | --------- | -------- |
| Chrome 113+  | ✓      | ✓         | ✓ 최우선 |
| Edge 113+    | ✓      | ✓         | ✓ 권장   |
| Firefox 141+ | ✓      | ✓         | ✓ 권장   |
| Safari 18+   | 제한적 | ✓         | △        |

WebGPU 미지원 시 자동으로 WebGL 2.0으로 폴백됩니다.
