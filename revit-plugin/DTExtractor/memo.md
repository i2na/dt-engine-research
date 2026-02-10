# DTExtractor 오류 수정 메모

## Export 오류 (InvalidObjectException)

**원인:** `CustomExporter.Export(View)` 실행 중 Revit 내부 파이프라인이 삭제되었거나 유효하지 않은 Element를 만나면 `InvalidObjectException`을 던짐. 또한 `DTMetadataCollector.ExtractElement`에서 `IsValidObject` 검증 없이 `FamilyInstance.Symbol`, `Document.GetElement(typeId)` 등에 접근하여 콜백 내부에서도 동일 예외 발생 가능. `OnPolymesh`/`OnInstanceBegin` 콜백에 try-catch가 없어 예외가 `Export()` 밖으로 전파되면 전체 내보내기가 중단됨.

**수정:**

- `DTMetadataCollector.ExtractElement`: 진입 시 `IsValidObject` 검증 추가, `FamilyInstance.Symbol` 및 `GetElement(typeId)` 결과에도 `IsValidObject` 체크.
- `DTGeometryExporter.OnPolymesh`, `OnInstanceBegin`: try-catch로 감싸서 예외가 Export 루프를 중단하지 않도록 방지. 오류 발생 시 로그 기록.
- `ExportCommand.cs`: `customExporter.Export(view3D)` 호출을 `Autodesk.Revit.Exceptions.InvalidObjectException` 전용 catch로 감싸서, Revit 내부 오류 발생 시에도 수집된 데이터를 `Serialize()`하여 부분 결과를 출력.

## Export 실패: Parquet 파일 미생성 및 GLB 1KB (빈 파일)

**증상:** Export 실행 시 `.glb` 파일은 1KB(빈 헤더만), `.parquet` 파일은 아예 생성되지 않음. UI에서 `FileNotFoundException` 발생. 로그에는 `"Parquet written."` 표시되지만 실제 파일 없음.

**로그 패턴:**

```
OnPolymesh failed for <GUID>: The referenced object is not valid...
(다수 반복)
GLB written.
Parquet written.   ← 거짓 로그
```

**근본 원인 (연쇄 실패):**

1. `OnPolymesh`에서 새 메시의 재질 추출 시 `ExtractMaterialData(_currentMaterial)`가 `materialNode.Color`, `.Transparency` 등 Revit API를 호출함. 해당 Material이 DB에서 삭제된 객체를 참조하면 `InvalidObjectException` 발생.
2. 예외가 `catch`로 잡히지만, 메시가 `_meshHashMap`에 캐시되지 않음 → 동일 지오메트리의 후속 폴리메시도 모두 "신규 메시"로 처리되어 같은 재질 오류 반복 → **전체 폴리메시 실패**.
3. 한 Element의 모든 폴리메시가 실패하면 `_currentElementHasGeometry`가 `false`로 유지 → `OnElementEnd`에서 `_metadataCollector.RemoveElement()` 호출 → 해당 Element의 메타데이터 삭제.
4. 모든 Element가 이렇게 삭제되면 `_records`가 비어서 `DTParquetWriter.Write()`가 `records.Count == 0`으로 즉시 반환 → **Parquet 파일 미생성**.
5. `Serialize()`는 `SerializeToParquet()` 반환 후 무조건 `"Parquet written."` 로그 기록 → **거짓 로그**.
6. `ExportCommand`에서 `new FileInfo(parquetPath).Length` 호출 시 파일이 없어 `FileNotFoundException` → **Revit 시스템 오류 다이얼로그**.

**수정:**

- `ExtractMaterialData`: try-catch 추가. 재질 접근 실패 시 `MaterialData.Default`(회색) 반환. 이를 통해 재질이 유효하지 않아도 지오메트리는 정상 추출됨.
- `_polymeshFailCount` 카운터 추가: 실패한 폴리메시 수를 추적하여 진단 로그 및 UI에 표시.
- `Serialize()`: `_records`가 비어있으면 Parquet 쓰기를 건너뛰고 `"WARNING: No valid element records collected. Parquet NOT created."` 로그 기록. GUID 일관성 체크도 건너뜀.
- `ExportCommand`: `File.Exists()` 체크 후 `FileInfo.get_Length()` 호출. Parquet 미생성 시 원인과 카운터를 포함한 `"Export Failed"` 다이얼로그 표시. 부분 성공 시에도 실패 폴리메시 수를 경고로 표시.

## Export 실패: 100% Polymesh 실패 (NotSupportedException - "지정한 메서드가 지원되지 않습니다")

**증상:** Export 실행 시 7,940개 Element가 처리되나, 156,998개 Polymesh 콜백이 **전부(100%)** 실패. Parquet 미생성, GLB에 유효 지오메트리 없음. UI에 `"Export completed but no usable geometry was extracted."` 표시.

**로그 패턴:**

```
OnPolymesh failed for <GUID>: 지정한 메서드가 지원되지 않습니다.
(156,998회 반복)
WARNING: No valid element records collected. Parquet NOT created. (polymesh failures=156998/156998)
```

**근본 원인 (System.Text.Json 런타임 어셈블리 충돌):**

1. `DTGltfBuilder.AddInstance()` 내에서 `System.Text.Json.JsonSerializer.SerializeToNode()`를 호출하여 GLB 노드에 GUID/이름 Extras를 기록함.
2. `SerializeToNode()`는 .NET 6+에서 추가된 API이며, 프로젝트는 `net48` 타겟. 컴파일 시점에는 SharpGLTF의 transitive `System.Text.Json` NuGet 의존성(6.0+)으로 API가 존재하나, **Revit 2024 런타임이 자체 번들한 구버전 `System.Text.Json.dll`을 우선 로드**하면서 `NotSupportedException` 발생.
3. `AddInstance()`가 **모든** polymesh 콜백의 코드 경로(캐시 히트/미스 무관)에서 호출되므로 100% 실패.
4. `AddInstance` 예외가 `OnPolymesh` catch로 전파 → `_currentElementHasGeometry`가 영원히 `false` → `OnElementEnd`에서 모든 메타데이터 삭제 → Parquet 미생성.
5. 추가적으로 `polymesh.GetNormals()`/`GetUVs()` 호출도 특정 모델에서 `NotSupportedException`을 던질 수 있으나, 이 케이스에서는 부차적 원인.

**수정:**

- `DTGltfBuilder.AddInstance()`: `JsonSerializer.SerializeToNode()` 제거 → `Newtonsoft.Json.JsonConvert.SerializeObject()` + `JsonNode.Parse()` 조합으로 교체. Extras 할당을 try-catch로 감싸 실패 시에도 인스턴스 생성은 유지.
- `DTGltfBuilder.AddInstance()`: `instanceNode.LocalMatrix` 세터를 try-catch로 감싸, 행렬 분해(TRS decomposition) 실패 시(미러 변환 등) Translation-only 폴백.
- `DTGltfBuilder.SerializeToGlb()`: 모델 Extras에도 동일한 `SerializeToNode` → `Newtonsoft.Json` + `JsonNode.Parse` 교체.
- `DTGeometryExporter.OnPolymesh()`: `polymesh.GetNormals()`와 `polymesh.GetUVs()` 호출을 개별 try-catch로 감싸, 법선 실패 시 면 정점에서 cross product로 flat normal 계산 폴백, UV 실패 시 null 처리.
- `DTGeometryExporter.OnPolymesh()`: catch 블록에 `ex.GetType().FullName`과 StackTrace 첫 줄을 기록하여 향후 진단 가능.
- `System.Text.Json` using 제거 (`JsonSerializer` 직접 사용 제거), `System.Text.Json.Nodes`만 유지.

## Export 실패: SharpGLTF VertexEmpty.SetBindings NotSupportedException

**증상:** 위 System.Text.Json 수정 이후에도 100% Polymesh 실패 지속. 에러 메시지 동일(`NotSupportedException`)이나 스택 트레이스가 다름.

**로그 패턴:**

```
OnPolymesh failed for <GUID>: [System.NotSupportedException] 지정한 메서드가 지원되지 않습니다.
  at: SharpGLTF.Geometry.VertexTypes.VertexEmpty.SetBindings(SparseWeight8& bindings)
(156,998회 반복)
```

**근본 원인 (SharpGLTF Toolkit MeshBuilder → Schema2 변환 버그):**

1. `DTGltfBuilder.AddMesh()`에서 `MeshBuilder<VertexPositionNormal, VertexTexture1>` 생성. 세 번째 제네릭 파라미터(스키닝)가 기본값 `VertexEmpty`로 추론됨.
2. `_model.CreateMesh(meshBuilder)` 호출 시 Toolkit 내부 변환 코드가 모든 버텍스 컴포넌트를 처리. 스키닝 슬롯의 `VertexEmpty.SetBindings(SparseWeight8&)` 호출됨.
3. `VertexEmpty`는 `IVertexSkinning` 인터페이스의 `SetBindings`를 `throw new NotSupportedException()`으로 구현. 스키닝 데이터가 없는 메시에서도 변환 과정에서 이 메서드가 호출되는 것이 alpha0031의 버그.
4. `AddMesh` 실패 → `AddInstance` 미호출 → `_currentElementHasGeometry = false` → `OnElementEnd`에서 메타데이터 삭제 → 전체 레코드 삭제.

**수정 (MeshBuilder 완전 우회):**

- `DTGltfBuilder.AddMesh()`: `MeshBuilder`/`VertexBuilder` 완전 제거. Schema2 직접 API(`Mesh.CreatePrimitive()` + `MeshPrimitive.WithVertexAccessor()`)로 교체. 이 경로는 Toolkit의 버텍스 타입 시스템을 전혀 거치지 않아 `VertexEmpty.SetBindings` 호출이 발생하지 않음.
    - `positions`/`normals`는 `Vector3[]` 배열로 직접 구성
    - `WithVertexAccessor("POSITION", positions)` → Schema2 Buffer/BufferView/Accessor 직접 생성
    - `WithIndicesAccessor(PrimitiveType.TRIANGLES, indices)` → 인덱스 버퍼 직접 생성
    - `WithMaterial(material)` → Schema2 Material 직접 할당
- `DTGltfBuilder.GetOrCreateMaterial()`: `MaterialBuilder` 유지 후 `_model.CreateMaterial(builder)` Toolkit 확장으로 Schema2 `Material`로 변환. Material 변환은 버텍스 타입과 무관하므로 안전.
- `using SharpGLTF.Geometry` 및 `using SharpGLTF.Geometry.VertexTypes` 제거.
- `DTGeometryExporter.OnElementEnd()`: `_metadataCollector.RemoveElement()` 호출 제거. 지오메트리 실패와 무관하게 메타데이터 보존.
- `DTGeometryExporter.Serialize()`: GUID 불일치를 예외가 아닌 로그 경고로 변경. Parquet는 메타데이터 레코드가 있는 한 항상 생성.

## 대용량 파일 Export 안정성 개선

**증상:** 큰 Revit 파일(수십만 요소)에서 Export 중 "응답없음" 발생. 작은 파일은 정상 작동.

**근본 원인:**

1. **로깅 I/O 오버헤드**: `File.AppendAllText()`를 매 콜백마다 호출 → 수십만 번의 파일 열기/닫기 반복 → I/O 병목.
2. **UI 스레드 블로킹**: 진행 상황 표시 없이 수분~수십분 처리 → Revit UI 응답없음으로 오인.
3. **메모리 누적**: 메시 중복 제거 해시맵이 무제한 증가 → 대규모 모델에서 수 GB 메모리 사용.
4. **과도한 Extras 직렬화**: 모든 인스턴스 노드에 GUID/이름 JSON 저장 → glTF 파일 비대화.

**수정:**

- `DTGeometryExporter`: `StreamWriter _logWriter` 필드 추가. 생성자에서 열고 `Serialize()`에서 닫음. `AutoFlush = false`로 버퍼링. 진행 로그만 명시적 `Flush()`.
- 진행 로그 간격: 5000 → 1000 요소마다. 실패 로그도 1000건마다 `Flush()`.
- `OnViewBegin`: `LevelOfDetail = 8 → 4`로 감소. 테셀레이션 밀도 절반 → 폴리곤 수 대폭 감소.
- 메시 중복 제거 캐시 상한: `MAX_MESH_CACHE = 10000`. 초과 시 신규 메시 생성하지만 해시맵에는 미등록.
- `DTGltfBuilder.AddInstance()`: 노드명에서 GUID 제거 (`inst_{count}`로 간소화). Extras JSON도 1000번째마다만 저장 → GLB 파일 크기 감소.
- `ExportCommand`: `TaskDialog` 진행 표시 추가 (향후 `IProgressIndicator` 연동 가능).

## "응답없음" 완전 제거 (IProgressIndicator)

**증상:** 대용량 파일 Export 중 "Revit이 응답하지 않습니다" 다이얼로그 표시. 백그라운드 처리는 진행되나 사용자가 멈춘 것으로 오인.

**근본 원인:** `CustomExporter.Export()`가 UI 스레드를 장시간 점유. Revit은 5초 이상 UI 업데이트가 없으면 "응답없음" 표시.

**수정:**

- `ExportCommand`: `IProgressIndicator` 구현 클래스 추가. 100개 요소마다 `Report(current, total)` 호출.
- `DTGeometryExporter`:
    - `_estimatedTotalElements` 필드 추가. 생성자에서 `FilteredElementCollector.GetElementCount()` 호출하여 총 요소 수 예측.
    - `SetProgressIndicator(IProgressIndicator)` 메서드 추가.
    - `OnElementBegin`에서 100개마다 `_progressIndicator?.Report(_elementCount, _estimatedTotalElements)` 호출 → Revit UI 응답 유지.
    - `Start`/`Finish`에서도 진행률 보고.
- `ExportProgressIndicator`: 5% 단위로 `.progress` 파일에 실시간 진행 상황 기록. 타임스탬프 + 경과 시간 표시.

**효과:**

- Revit이 주기적으로 UI 업데이트 → "응답없음" 다이얼로그 표시 안 됨.
- 사용자는 `.export-log.txt.progress` 파일을 모니터링하여 실시간 진행 상황 확인 가능.
- 몇 시간 걸리는 초대형 모델도 안정적으로 처리 가능.

## "닫힌 TextWriter" 예외 수정

**증상:** Export 성공 후에도 Revit 오류 다이얼로그 표시: `"닫힌 TextWriter에는 쓸 수 없습니다"` (Cannot write to a closed TextWriter).

**근본 원인:** `DTGeometryExporter.Serialize()` 내에서 `_logWriter.Close()`를 호출. 이후 `ExportCommand.Execute()`에서 `exporter.LogTiming()` 호출 시 이미 닫힌 StreamWriter에 쓰기 시도.

**수정:**

- `Serialize()`에서 `_logWriter.Close()` 제거
- `CloseLog()` 메서드 추가 (disposed guard 포함)
- `LogError()`/`LogTiming()`에 `_logClosed` 가드 추가
- `ExportCommand.Execute()`에 `finally { exporter?.CloseLog(); }` 블록 추가 → 모든 경로에서 안전하게 닫힘

## 20% 조기 중단 수정

**증상:** 대용량 모델에서 Export가 ~20%에서 중단. Revit "응답없음" 표시. GLB/Parquet 미생성.

**근본 원인:**

1. `OnPolymesh`의 100% 실패 감지 로직이 `throw InvalidOperationException`을 던짐
2. `ShouldStopOnError = false`에서 Revit이 이 예외를 내부적으로 catch하여 export 상태를 불안정하게 만듦
3. `ExportProgressIndicator.Close()`가 시간 기반 휴리스틱으로 "Export phase completed" 거짓 메시지 기록

**수정:**

- 100% 실패 시 `throw` 제거 → 경고 로그만 기록. Export는 끝까지 진행하여 부분 데이터 수집
- `ExportProgressIndicator.Close()`: 성공/실패 판단 제거 → `"Export phase ended"` 중립 메시지로 변경

## 시각적 충실도 개선 (Flat Gray → PBR)

**증상:** GLB 파일이 웹 뷰어에서 평평한 회색으로 렌더링. 깊이감, 재질 색상, 반사 없음.

**근본 원인:**

1. `ExtractMaterialData`가 `MaterialNode.Color`/`Transparency`/`Smoothness`만 추출 → Metallic 값 없이 항상 0
2. 재질 키가 Color만으로 생성 → 같은 색이지만 다른 투명도/광택의 재질이 하나로 병합
3. `DoubleSided = false` (기본값) → 뒷면이 투명하게 렌더링 → 벽/바닥이 한쪽에서 보이지 않음
4. `OnEachFacet` 법선 분기에서 bounds check 없음 → `IndexOutOfRangeException` → polymesh 실패

**수정:**

- `MaterialData`에 `Metallic` 속성 추가 (기본값 0.0)
- `ExtractMaterialData`: Revit `AppearanceAssetElement.GetRenderingAsset()`에서 `generic_is_metal`/`generic_glossiness` 속성 추출 (reflection 기반, 안전한 fallback)
- 재질 키: Color + Transparency + Metallic + Smoothness 모두 포함
- `GetOrCreateMaterial`: `material.DoubleSided = true` 설정, `metallic` 파라미터에 `data.Metallic` 사용
- `OnEachFacet` 법선: bounds check 추가, 범위 초과 시 cross product fallback

## 요소 선택 및 데이터 매핑 개선

**증상:** 웹 뷰어에서 대부분의 건물 요소(벽, 문 등)가 클릭에 반응하지 않음. Topography 같은 큰 요소만 선택 가능.

**근본 원인:**

1. 노드 이름이 `inst_{count}`로 설정되어 요소 식별 불가
2. Extras(GUID/이름)가 1000번째마다만 저장 → 대부분의 노드에 식별 정보 없음
3. 뷰어가 노드를 클릭해도 Parquet 메타데이터와 연결할 방법 없음

**수정:**

- `DTGltfBuilder.AddInstance()`: 노드 이름을 GUID(UniqueId)로 설정
- `DTGltfBuilder.AddInstance()`: `% 1000` 스킵 로직 제거 → **모든** 인스턴스에 `{ guid, name }` Extras 저장
- 뷰어에서 노드 클릭 → 노드 이름(=GUID) → Parquet에서 메타데이터 조회 가능
- 동일 요소의 여러 폴리메시 노드가 같은 GUID → 뷰어가 같은 GUID의 모든 노드를 하이라이트 가능

---

## v2.0 아키텍처 리팩토링 (2026-02-09)

위에 기록된 모든 이슈를 근본적으로 해결하기 위해, `revit-glTF-exporter` 오픈소스 프로젝트의 검증된 패턴을 참조하여 Export 로직을 완전 재설계함.

### 변경된 아키텍처

**기존 (Polymesh-per-Node):**

```
OnPolymesh → AddMesh → AddInstance(새 노드) → 1 Element = N개 노드
```

하나의 BIM 요소에 수십 개의 glTF 노드가 생성되어 파일 비대화, 선택 혼란, 메모리 폭증.

**신규 (Element-per-Node, 레퍼런스 패턴):**

```
OnElementBegin → 버퍼 초기화
OnMaterial → 현재 재질 갱신
OnPolymesh × N → 재질별 버퍼에 축적 (월드 좌표 변환 적용)
OnElementEnd → AddElement() → 1 Element = 1 노드 (다중 Primitive)
```

하나의 BIM 요소가 정확히 하나의 glTF 노드로 매핑. 노드 이름 = UniqueId.

### 핵심 수정 사항

| 항목                       | 기존 문제                            | 수정                                                              |
| -------------------------- | ------------------------------------ | ----------------------------------------------------------------- |
| **시각 (Flat Gray)**       | sRGB 컬러를 그대로 glTF에 저장       | `SrgbToLinear()` 변환 적용                                        |
| **시각 (Roughness)**       | `Smoothness`(0-100)를 0-1로 착각     | `roughness = 1 - (Smoothness / 100)`                              |
| **시각 (법선)**            | 법선 벡터에 Transform 미적용         | `transform.OfVector(n).Normalize()` 적용                          |
| **시각 (AppearanceAsset)** | Reflection 기반 접근                 | `AssetPropertyDoubleArray4d` 직접 캐스팅 + Schema 기반 Color 조회 |
| **안정성 (크래시)**        | Polymesh마다 노드 생성 → 메모리 폭증 | Element별 버퍼 축적 후 OnElementEnd에서 일괄 플러시               |
| **안정성 (TextWriter)**    | Serialize에서 로그 닫음              | CloseLog() 분리, finally 블록에서 안전하게 닫힘                   |
| **선택 (GUID)**            | 1 Element = N 노드, 식별 혼란        | 1 Element = 1 노드, 노드명 = UniqueId, 모든 노드에 Extras         |
| **링크 모델**              | 미지원                               | OnLinkBegin/End에서 Document 전환 + Transform 스택 유지           |
| **재질 캐싱**              | Color만으로 키 생성                  | Color + Transparency + Metallic + Roughness 전부 포함             |
| **DoubleSided**            | false (기본)                         | true (벽/바닥 양면 렌더링)                                        |
| **Alpha**                  | 항상 OPAQUE                          | `Transparency > 0`이면 BLEND 모드 자동 적용                       |

### 변경된 파일

- `Core/DTGeometryExporter.cs` — IExportContext 완전 재작성
- `Core/DTGltfBuilder.cs` — Element-level API + sRGB→Linear + PBR
- `DTExtractor.csproj` — PostBuild에 MakeDir 추가

### 유지된 파일 (변경 없음)

- `Commands/ExportCommand.cs` — API 호환 유지
- `Core/DTMetadataCollector.cs` — 7종 파라미터 추출 유지
- `Core/DTParquetWriter.cs` — Parquet 직렬화 유지
- `Core/IDTProgressIndicator.cs` — 인터페이스 유지
- `Models/DTElementRecord.cs` — 데이터 모델 유지
- `Models/DTParameterRecord.cs` — 데이터 모델 유지
- `App.cs` — 리본 탭 유지
- `DTExtractor.addin` — 매니페스트 유지

---

## 실행 가이드 (Execution Guide)

### 1. 빌드 환경 요구사항

| 항목      | 요구                                      |
| --------- | ----------------------------------------- |
| **IDE**   | Visual Studio 2022 (17.x)                 |
| **SDK**   | .NET Framework 4.8 Developer Pack         |
| **Revit** | Autodesk Revit 2024 (또는 2023) 설치 필수 |
| **NuGet** | 빌드 시 자동 복원                         |

### 2. Visual Studio 2022에서 빌드하기

1. **솔루션 열기**: `revit-plugin\DTExtractor\DTExtractor.csproj`를 Visual Studio 2022에서 엽니다.

2. **빌드 구성 선택**: 상단 툴바에서 Configuration을 선택합니다:
    - Revit 2024: `Debug-R2024` 또는 `Release-R2024`
    - Revit 2023: `Debug-R2023` 또는 `Release-R2023`

3. **Revit API 참조 확인**: `.csproj`가 Revit 설치 경로를 참조합니다:

    ```
    C:\Program Files\Autodesk\Revit 2024\RevitAPI.dll
    C:\Program Files\Autodesk\Revit 2024\RevitAPIUI.dll
    ```

    Revit이 다른 경로에 설치되었다면 `.csproj`의 `HintPath`를 수정하세요.

4. **NuGet 패키지 복원**: 빌드 시 자동으로 복원됩니다. 수동: `도구 > NuGet 패키지 관리자 > 솔루션 NuGet 패키지 복원`.

5. **빌드 실행**: `Ctrl+Shift+B` 또는 `빌드 > 솔루션 빌드`.

### 3. 자동 배포 (Post-Build Event)

빌드가 성공하면 **Post-Build 이벤트**가 자동 실행됩니다:

1. **대상 폴더 생성**: `%AppData%\Autodesk\Revit\Addins\{RevitVersion}\` 폴더가 없으면 자동 생성.
2. **기존 파일 삭제**: 해당 폴더에서 `DTExtractor.dll`, `DTExtractor.addin`, 의존성 DLL 삭제.
3. **신규 파일 복사**: 빌드 출력의 모든 `.dll` 파일과 `DTExtractor.addin`을 해당 폴더로 복사.

**배포 확인 방법:**

```
%AppData%\Autodesk\Revit\Addins\2024\
├── DTExtractor.dll          ← 메인 플러그인
├── DTExtractor.addin        ← Revit 매니페스트
├── SharpGLTF.Core.dll       ← glTF 라이브러리
├── SharpGLTF.Toolkit.dll    ← glTF Toolkit
├── Parquet.Net.dll           ← Parquet 라이브러리
├── Newtonsoft.Json.dll       ← JSON 직렬화
└── (기타 의존성 DLL)
```

Windows 탐색기에서 `%AppData%\Autodesk\Revit\Addins\2024\` 경로를 직접 입력하여 파일 존재를 확인할 수 있습니다.

**`DTExtractor.addin`의 `<Assembly>` 경로:**

```xml
<Assembly>DTExtractor.dll</Assembly>
```

이는 상대 경로이며, `.addin` 파일과 `.dll`이 같은 폴더에 있으므로 정확합니다.

### 4. Revit에서 플러그인 실행하기

1. **Revit 시작**: Revit 2024 (또는 2023)을 실행합니다.

2. **플러그인 확인**: 리본 탭에 **"DT Engine"** 탭이 표시되는지 확인합니다. 표시되지 않으면 `Addins` 폴더에 파일이 올바르게 복사되었는지 확인하세요.

3. **3D 뷰 활성화**: Export는 3D 뷰에서만 동작합니다. 활성 뷰가 3D 뷰가 아니면 자동으로 기본 `{3D}` 뷰를 찾습니다.

4. **Export 실행**:
    - 리본: **DT Engine** > **Export** > **Export to DT Engine** 클릭
    - 파일 저장 대화상자에서 `.glb` 파일 경로를 지정합니다.
    - Export가 시작되며 진행 상황이 로그에 기록됩니다.

5. **출력 파일**:
   | 파일 | 설명 |
   |------|------|
   | `model.glb` | glTF 2.0 바이너리 (PBR 재질, 노드명=UniqueId) |
   | `model.parquet` | Apache Parquet (7종 파라미터, GUID Primary Key) |
   | `model.export-log.txt` | 진단 로그 (요소 수, 실패 수, 타이밍) |
   | `model.export-log.txt.progress` | 실시간 진행률 (5% 단위) |

6. **Export 완료 확인**: 성공 시 TaskDialog에 파일 크기와 요소 수가 표시됩니다. 부분 실패 시 경고와 함께 실패한 polymesh 수가 표시됩니다.

### 5. 웹 뷰어에서 테스트하기

1. 생성된 `.glb` 파일을 `web-viewer/public/models/` 폴더에 복사합니다.
2. 웹 뷰어를 실행하여 3D 모델이 올바르게 렌더링되는지 확인합니다.
3. **시각 확인**: 재질 색상, 반사, 깊이감이 있는지 (flat gray가 아닌지) 확인합니다.
4. **선택 확인**: 모델의 요소(벽, 문, 기둥 등)를 클릭하면 해당 요소의 GUID가 식별되는지 확인합니다.
5. **데이터 연동**: 선택된 요소의 GUID로 `.parquet` 파일에서 메타데이터 조회가 되는지 확인합니다.

### 6. 트러블슈팅

| 증상                           | 원인                          | 해결                                           |
| ------------------------------ | ----------------------------- | ---------------------------------------------- |
| 리본 탭 미표시                 | `.addin`/`.dll` 미배포        | Addins 폴더 확인, 빌드 재실행                  |
| 빌드 실패 "RevitAPI not found" | Revit 미설치 또는 경로 불일치 | `.csproj`의 `HintPath` 수정                    |
| Export 0% 중단                 | 3D 뷰 없음                    | Revit에서 3D 뷰 생성                           |
| GLB 빈 파일                    | 100% polymesh 실패            | `.export-log.txt` 확인, Revit 재시작 후 재시도 |
| Flat gray 렌더링               | 웹 뷰어가 PBR 미지원          | Three.js MeshStandardMaterial 사용 확인        |
| 선택 미동작                    | 노드 이름 매핑 오류           | GLB 파일의 노드 이름이 UniqueId인지 확인       |

---

## 흰색 렌더링 수정 (White Rendering)

**증상:** 웹 뷰어에서 재질이 흰색으로 보이거나, 얇은 BIM 요소(벽·바닥·천장) 뒷면이 잘려 보이는 현상.

**근본 원인 (두 가지):**

1. **재질 스키마 커버리지 부족 (Revit Exporter)**  
   `DTGeometryExporter.cs`의 `GetAppearanceColor()`가 Revit 재질 스키마 27종 이상 중 13종만 인식. 유리(glass), 목재(wood), 돌(stone), 소프트우드(softwood), 거울(mirror), 물(water) 등 미인식 스키마는 `null`을 반환하고, 내보내기가 `materialNode.Color`로 폴백. 해당 값이 많은 재질에서 순백(255,255,255)이라, 렌더링 에셋이 아닌 기본 셰이딩 색만 쓰여 흰색으로 보임.

2. **FrontSide 강제 (Web viewer)**  
   `GLBLoader.ts`의 `setupScene()`에서 모든 `MeshStandardMaterial`에 `mat.side = THREE.FrontSide`를 강제 적용. glTF 내보내기의 `DoubleSided = true`를 덮어써, 얇은 BIM 요소의 뒷면이 컬링되어 보이지 않는 현상 발생.

**수정:**

**DTGeometryExporter.cs**

- `ColorPropertyMap`: 13종 → 27종으로 확장. 추가 스키마: PrismWoodSchema, PrismGlazingSchema, PrismStoneSchema, PrismTransparentSchema, PrismMirrorSchema, PrismSoftwoodSchema, GlazingSchema, WoodSchema, StoneSchema, SolidGlassSchema, MirrorSchema, WaterSchema, SoftwoodSchema.
- `GetAppearanceColor()` 3단계 폴백으로 재작성:
    1. 스키마 기반 조회 (기존 로직, 스키마 범위 확대)
    2. 잘 알려진 폴백 속성명: `generic_diffuse`, `opaque_albedo`, 공통 Tint/Shade_Color
    3. 속성 스캔: 에셋의 모든 속성 중 `AssetPropertyDoubleArray4d`이고 이름에 "color", "diffuse", "albedo", "tint" 포함된 것 탐색 (순백 값은 제외해 오탐 방지)
- 중복 제거를 위해 `TryReadColorProperty` 헬퍼 추출.

**GLBLoader.ts**

- `mat.side = THREE.FrontSide` 강제 제거 → glTF의 DoubleSide 설정 유지.
- `MeshBasicMaterial` 변환 시에도 원본 재질의 `side`를 상속하고, FrontSide 하드코딩 제거.
