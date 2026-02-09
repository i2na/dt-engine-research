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
