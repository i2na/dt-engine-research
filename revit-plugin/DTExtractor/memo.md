## 근본 원인 분석

### (a) 1개 Element / 1개 Polymesh만 처리되고, 해당 Polymesh의 Geometry가 GLB에 없는 원인

**3가지 원인이 복합적으로 작용합니다:**

1. **`CustomExporter.Export()`가 Transaction 내부에서 실행됨** — Revit API 문서는 `CustomExporter.Export()`를 열린 Transaction 안에서 호출하면 안 된다고 명시합니다. Transaction이 활성 상태에서 Export를 실행하면 내부적으로 요소 참조가 무효화되거나, 에러가 발생하여 export가 조기 종료됩니다. `ShouldStopOnError = false`이므로 예외가 표면화되지 않고, 결과적으로 극소수의 요소만 처리됩니다.

2. **`Get3DView()`가 첫 번째 비템플릿 3D 뷰를 반환** — 이것이 Section Box가 활성화된 뷰이거나, 가시성 필터가 걸린 뷰일 수 있습니다. 기본 `{3D}` 뷰나 활성 뷰를 우선 사용해야 합니다.

3. **`OnPolymesh`에서 Normal 인덱싱 오류** — `DistributionOfNormals`이 `AtEachPoint`가 아닌 경우 (`OnePerFace` 또는 `OnEachFacet`), Normal 배열 크기가 Vertex 배열과 다릅니다. `DTGltfBuilder.AddMesh` 내부에서 `normals[indices[i]]`로 접근 시 `IndexOutOfRangeException`이 발생하며, 이 예외가 CustomExporter 프레임워크에 의해 삼켜져 geometry가 GLB에 추가되지 않습니다.

### (b) "The referenced object is not valid" 에러

**`_currentElement`(Revit `Element` 객체 참조)를 콜백 간에 유지하는 것이 원인입니다.** Transaction 내부에서 Export를 실행하면 문서 상태 변경으로 Element 참조가 무효화됩니다. `OnElementBegin`에서 획득한 `Element`를 `OnPolymesh`에서 `_currentElement.Name`으로 접근할 때 이 에러가 발생합니다.

### 추가로 발견된 버그들

- **DTGltfBuilder.AddMesh**: 메시를 Scene Node로 생성하고, AddInstance에서 또 Scene Node를 생성 → 모든 geometry가 두 번 나타남 (이중 렌더링)
- **DTGltfBuilder 인스턴싱**: 월드 스페이스 좌표로 해시 → 같은 패밀리의 다른 인스턴스가 공유되지 않음
- **Transform 스택 미사용**: 중첩된 `OnInstanceBegin` 시 외부 transform이 덮어씌워짐
- **DTParquetWriter**: `schema.DataFields[4]`가 두 번 사용됨 (typeNames에 familyNames 데이터 기록)
- **DTMetadataCollector.GetArea**: `element.GetParameter()` 사용 (올바른 API: `element.get_Parameter()`)
- **GLB/Parquet GUID 불일치**: Geometry가 없는 요소도 Parquet에 기록되어 항상 불일치 발생

## 변경 사항 요약

### 1. `ExportCommand.cs` — Transaction 제거 + 뷰 선택 개선

**근본 원인 해결: Export가 Transaction 내에서 실행되어 1개 element만 처리됨**

- `[Transaction(TransactionMode.Manual)]` → `[Transaction(TransactionMode.ReadOnly)]` — 이 커맨드는 문서를 수정하지 않음
- **Transaction 블록 전체 제거** — `CustomExporter.Export()`는 Revit API 문서에 따라 열린 Transaction 밖에서 호출해야 함. Transaction이 Element 참조를 무효화하고, export 파이프라인을 조기 종료시키는 주원인
- `Finish()` → `Serialize()` 호출로 변경 — `Finish()`는 `CustomExporter.Export()` 내부에서 자동 호출되므로, 파일 직렬화는 별도 `Serialize()` 메서드로 분리
- `Get3DView()` → `GetExportView()` — 우선순위: (1) 활성 3D 뷰 (사용자 의도 존중), (2) 기본 `{3D}` 뷰 (Revit이 자동 생성, 모든 요소 포함), (3) 임의의 비템플릿 3D 뷰. 기존 코드는 첫 번째 비템플릿 뷰를 무조건 반환하여 Section Box나 가시성 필터가 걸린 뷰가 선택될 수 있었음

### 2. `DTGeometryExporter.cs` — Element 참조 제거 + Normal 처리 + Transform 스택

**근본 원인 해결: "referenced object is not valid" + GLB에 geometry가 없는 현상**

- **`_currentElement` 필드 제거** → `_currentGuid`(string) + `_currentName`(string)만 유지. `OnElementBegin`에서 `Element`의 필요한 데이터를 즉시 추출하고, Revit `Element` 참조를 콜백 간에 유지하지 않음. "referenced object is not valid" 에러의 직접 원인 해결
- **`_transformStack`(Stack\<Transform\>) 도입** — `OnInstanceBegin`에서 부모 transform과 곱하여 누적 push, `OnInstanceEnd`에서 pop. 중첩된 FamilyInstance에서 외부 transform이 덮어씌워지는 버그 수정
- **`OnPolymesh` Normal 분포 처리** — `DistributionOfNormals`를 확인하여 3가지 경우 모두 처리:
    - `AtEachPoint`: 기존 인덱싱 유지 (normal count == point count)
    - `OnePerFace`: 단일 normal을 모든 vertex에 복제
    - `OnEachFacet`: per-facet vertex normal 사용 (facet \* 3 인덱싱)
    - 기존 코드는 항상 `AtEachPoint`를 가정하여 `normals[indices[i]]`로 접근 → `IndexOutOfRangeException` 발생 → CustomExporter가 삼키고 geometry 미등록
- **로컬 스페이스 유지** — vertex를 월드 스페이스로 변환하지 않고 원본(로컬) 좌표 유지. 인스턴스 node의 transform이 배치를 담당. 같은 패밀리의 다른 인스턴스가 메시를 공유 가능
- **`ComputeMeshHash` 개선** — 로컬 스페이스 점 + 토폴로지(facet indices)를 함께 해싱. 기존 코드는 월드 스페이스 좌표 + index count만 사용하여 인스턴싱이 사실상 불가능했음
- **`OnElementEnd`에서 geometry 미생성 요소를 Parquet에서 제거** — `_currentElementHasGeometry` 플래그로 추적. GLB-Parquet GUID 일치성 보장
- **`Finish()` / `Serialize()` 분리** — `Finish()`는 IExportContext 콜백(경량 로그만), `Serialize()`는 실제 파일 쓰기 + GUID 검증. 이중 호출 문제 해결. GUID 불일치 시 silent return 대신 상세 에러 + 로그

### 3. `DTGltfBuilder.cs` — 이중 렌더링 버그 수정

- `Dictionary<int, Node> _meshNodes` → `Dictionary<int, Mesh> _meshes` — `AddMesh`가 scene node를 생성하지 않고 mesh만 저장. `AddInstance`에서만 scene node 생성. 기존 코드는 `AddMesh`에서 node 1개 + `AddInstance`에서 node 1개 = 모든 요소가 두 번 렌더링됨
- 인스턴스 노드 이름에 고유 인덱스 추가 (`inst_{guid}_{count}`) — 동일 element의 다중 polymesh 충돌 방지

### 4. `DTMetadataCollector.cs` — RemoveElement 추가

- `RemoveElement(string guid)` 메서드 추가 — `OnElementEnd`에서 geometry가 없는 요소를 Parquet 레코드에서 제거하여 GLB-Parquet GUID 일치성 보장

---

## 커밋 메시지 추천

- 커밋 메시지  
  `fix(DTExtractor): export pipeline and GLB/Parquet consistency`
    - Transaction 제거 후 Export 호출 (API 준수)
    - GetExportView로 활성/기본 3D 뷰 우선 사용
    - Element 참조 제거, Normal 분포 처리, Transform 스택, 인스턴싱 해시 개선
    - AddMesh 이중 노드 제거, geometry 없는 요소 Parquet에서 제외

---

## Revit에서 애드인 실행 방법

1. **프로젝트(.rvt) 열기** — 애드인은 열린 문서가 있어야 동작함.
2. **(권장) 3D 뷰로 전환** — 리본 **View → 3D View** 또는 프로젝트 브라우저에서 `{3D}` 등 3D 뷰 선택. Export 시 이 뷰의 가시성/Section Box가 반영됨. 3D 뷰가 없으면 "No suitable 3D view found" 에러.
3. **리본에서 실행** — **DT Engine** 탭 → **Export** 패널 → **Export to DT Engine** 버튼 클릭.
4. **저장 대화상자** — GLB 저장 경로 선택 후 저장하면 같은 경로에 `.glb`, `.parquet`, `.export-log.txt` 생성됨.
