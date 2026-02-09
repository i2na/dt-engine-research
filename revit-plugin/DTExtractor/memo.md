# DTExtractor 오류 수정 메모

## 1. 빌드 오류 (MSB3021 / MSB3030)

**원인:** PostBuild 타겟이 `%AppData%\Autodesk\Revit\Addins\2024\`에 DLL을 삭제 후 복사하는데, Revit이 실행 중이면 해당 파일이 잠겨(lock) `Delete`/`Copy`가 실패하여 빌드 자체가 중단됨.

**수정:** `DTExtractor.csproj`의 `Delete`와 `Copy` 태스크에 `ContinueOnError="WarnAndContinue"` 추가. 파일이 잠겨 있으면 경고만 출력하고 빌드는 성공함. Revit 재시작 후 다음 빌드에서 정상 복사됨.

## 2. Export 오류 (InvalidObjectException)

**원인:** `CustomExporter.Export(View)` 실행 중 Revit 내부 파이프라인이 삭제되었거나 유효하지 않은 Element를 만나면 `InvalidObjectException`을 던짐. 또한 `DTMetadataCollector.ExtractElement`에서 `IsValidObject` 검증 없이 `FamilyInstance.Symbol`, `Document.GetElement(typeId)` 등에 접근하여 콜백 내부에서도 동일 예외 발생 가능. `OnPolymesh`/`OnInstanceBegin` 콜백에 try-catch가 없어 예외가 `Export()` 밖으로 전파되면 전체 내보내기가 중단됨.

**수정:**

- `DTMetadataCollector.ExtractElement`: 진입 시 `IsValidObject` 검증 추가, `FamilyInstance.Symbol` 및 `GetElement(typeId)` 결과에도 `IsValidObject` 체크.
- `DTGeometryExporter.OnPolymesh`, `OnInstanceBegin`: try-catch로 감싸서 예외가 Export 루프를 중단하지 않도록 방지. 오류 발생 시 로그 기록.
- `ExportCommand.cs`: `customExporter.Export(view3D)` 호출을 `Autodesk.Revit.Exceptions.InvalidObjectException` 전용 catch로 감싸서, Revit 내부 오류 발생 시에도 수집된 데이터를 `Serialize()`하여 부분 결과를 출력.
