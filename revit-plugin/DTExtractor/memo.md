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
