# CLAUDE.md

## Quy tắc vàng (BẮT BUỘC)

- **Trong lúc owner đi ngủ, Claude tự quyết mọi việc, KHÔNG hỏi owner bất cứ điều gì**
  -- kể cả thêm / xoá / đổi tên file, chọn thư viện, chọn thuật toán, đổi kiến trúc.
  Gặp chỗ phải chọn: tự chọn phương án tốt nhất, ghi lý do vào commit / README, làm tiếp.
- **Owner bắt đầu làm việc lúc 06:00 sáng** (giờ Việt Nam, UTC+7) và sẽ kiểm tra kết quả
  lúc đó. Trước 06:00 mọi thứ phải: build được, có README cập nhật, có tóm tắt những gì
  đã làm / chưa làm / cần owner test tay (ví dụ với máy scan thật).
- App dùng để **bán thương mại**: mọi thư viện / tool / dữ liệu đưa vào phải có license
  cho phép dùng thương mại (MIT, BSD, Apache-2.0...). Tránh GPL / AGPL. Ghi rõ license
  và rủi ro bằng sáng chế trong `THIRD-PARTY-NOTICES.md`.
- Không tự ý commit code.
- Việc đầu tiên trước mỗi đợt làm: cập nhật README.

## Ghi chú tách repo (2026-10-06)

Repo này chỉ chứa **app mobile** (`DocScanner`, `DocScanner.Core`, `DocScanner.Shared`, `DocScanner.Core.Tests`), tách
ra từ monorepo `ImageProcessing` (vốn còn có app desktop `ImageOptimizerTool`/`ImageCoreService` -- không liên quan gì
tới repo này, đã bỏ hết nội dung đó ra khỏi file này). Vài điểm cần biết khi đọc lịch sử bên dưới:

- Mọi đường dẫn `Source/...` trong các mục lịch sử dưới đây là đường dẫn CŨ (trong monorepo). Trong repo này, bỏ hẳn
  tiền tố `Source/` -- ví dụ `Source/DocScanner/DocScanner.csproj` giờ là `DocScanner/DocScanner.csproj`.
- Project này trước đây tên `ImageCore.Shared` (dùng chung với app desktop) -- trong repo này đã đổi tên thư mục/project
  thành `DocScanner.Shared`, nhưng code bên trong vẫn giữ `namespace ImageCoreService` (chưa đổi, chỉ là tên namespace
  cũ, không ảnh hưởng chức năng).
- **`MOBILE-STATUS.md` và `THIRD-PARTY-NOTICES.md`** được nhắc nhiều lần bên dưới nhưng **KHÔNG có trong repo này**
  (không tìm thấy ở repo cũ, không có lịch sử git để khôi phục) -- coi như đã mất; CLAUDE.md này là nguồn duy nhất còn lại.
- **Bộ unit test riêng cho tầng thuật toán `DocScanner.Shared`** (lịch sử gọi là `ImageCore.Shared.Tests`: dò mép giấy
  qua 19 cảnh giả lập, nắn phối cảnh, Sauvola, PngWriter, test chống "mép chữ gợn sóng"...) ban đầu không có trong repo
  này -- **owner đã tự bổ sung lại `DocScanner.Shared.Tests`** (2026-10-06). Project copy sang thiếu `ProjectReference`
  tới `DocScanner.Shared.csproj` nên không build được (44 lỗi `CS0246` -- `Quad`/`RgbImage`/`GrayImage` không tìm thấy);
  đã thêm lại tham chiếu đó + đăng ký project vào `DocScanner/DocScanner.slnx`. `dotnet test DocScanner.Shared.Tests`:
  **135/135 PASS** (đúng số lượng cuối cùng ghi trong lịch sử dưới đây) -- tầng thuật toán lõi đã có lại lưới an toàn test.
- Đã sửa 1 lỗi phát hiện khi rà soát: `DocScanner.Core.csproj` có `ProjectReference` trỏ ra ngoài repo
  (`..\..\..\USC\ImageProcessing\DocScanner\DocScanner.Shared\...`, còn sót từ monorepo cũ) cạnh tham chiếu nội bộ đúng
  -- đã xoá dòng trỏ ra ngoài, chỉ còn tham chiếu `..\DocScanner.Shared\DocScanner.Shared.csproj`.
- Tổng test hiện tại của repo: `DocScanner.Core.Tests` 196 + `DocScanner.Shared.Tests` 135 = 331, build Android (Debug)
  0 lỗi -- kiểm tra lại toàn bộ 2026-10-06 sau các sửa trên.

## App mobile (đợt 2026-09-25, kế hoạch đã chốt với owner)

- App: `DocScanner` (solution mobile `DocScanner/DocScanner.slnx`). Package `btk.docscanner`, tên hiển thị "Doc Scanner"
  (không dùng tên IMIP, app không liên quan công ty). Máy test thật: Samsung Galaxy Note 10+.
- Framework: **.NET MAUI** (net10.0-android), làm **Android trước** (owner không có Mac, iOS để sau).
- Bản đầu: nhập ảnh từ thư viện / chụp camera -> tự dò mép giấy hoặc kéo 4 điểm -> cắt phối cảnh
  -> đen trắng (Sauvola) -> **nhiều trang + xuất PDF**. **Chưa có OCR.**
- Dò mép giấy tự viết bằng C# sau interface `IEdgeDetector` (không OpenCV / Emgu vì license).

| Bước | Nội dung | Trạng thái |
|---|---|---|
| 1 | Tách `ImageCore.Shared` khỏi System.Drawing, xUnit | **xong** (SmokeTests ALL PASS, 10 unit test PASS) |
| 2 | Khung app MAUI, quyền camera / thư viện | **xong**, chạy được trên Note 10+ |
| 3 | Nhập ảnh (thư viện, camera, EXIF, giảm ảnh lớn) | **xong**, thử trên Note 10+ (thư viện OK; camera chưa thử, xem ghi chú Bước 3) |
| 4 | Tự dò mép giấy | **xong**; test mô phỏng PASS, mới thử 2 ảnh thật (1 đúng, 1 sai: tờ bị khung cắt mép) -> còn phải chỉnh cho ảnh thật |
| 5 | Kéo 4 điểm + kính lúp | **xong, thử trên Note 10+** (riêng nút Xoay 90° mới có unit test, chưa bấm thử trên máy) |
| 6 | Cắt phối cảnh (homography), khổ A4, kéo điểm ra ngoài ảnh | **xong**; ảnh thật nắn thẳng đúng trên Note 10+; A4 và kéo ra ngoài ảnh chưa được owner xác nhận; chưa thử 48 MP thật |
| 7 | Đen trắng (Sauvola / Otsu), giới hạn RAM | **xong** (unit test + PC); chưa thử trên máy |
| 8 | Nhiều trang, sắp xếp, xuất PDF, chia sẻ | **xong** (unit test + PDFium); chưa thử trên máy |
| 9-10 | Hoàn thiện, test máy thật, phát hành Google Play | chưa |

### Bước 1: ghi chú
- Đã chuyển sang Shared: `GrayImage`, `Binarizer`, `EnumDescriptionConverter`, phần phân tích của
  `DocumentCleanup` (DetectSkew, DetectContentBounds, Despeckle, Percentile).
- Ở lại `ImageCoreService`: `GdiGray` (FromBitmap, ToBitmap8bpp / 1bpp) và `BitmapTransforms`
  (RotateArbitrary, RotateRight, Crop). Call site cũ chỉ đổi tên lớp; namespace vẫn `ImageCoreService`.
- `PageAnalyzer` (IsBlank / Classify) chưa chuyển vì `Classify` còn dùng Bitmap; chuyển khi mobile cần.
- Cần lưu ý cho bước 7: Sauvola dùng 2 integral image kiểu `long` (~16 byte/pixel). Ảnh 8,7 MP tốn
  ~140 MB, phải xử lý theo dải hoặc dùng bản tiết kiệm RAM trước khi chạy trên máy 3 GB.

### Bước 2: ghi chú
- `DocScanner`: chỉ target `net10.0-android`, min SDK 26 (Android 8). Đã xoá Platforms iOS / MacCatalyst /
  Windows của template; khi làm iOS thì tạo lại từ `dotnet new maui`.
- MVVM (CommunityToolkit.Mvvm, MIT) + DI trong `MauiProgram`; Shell routes `viewer` / `crop` / `export` đang là
  trang giữ chỗ (`Views/PlaceholderPages.cs`), mỗi bước sau thay bằng trang thật.
- Quyền: chỉ `CAMERA` (+ `queries` IMAGE_CAPTURE cho Android 11+). Nhập từ thư viện dùng system photo picker
  nên không cần quyền lưu trữ. Cố ý KHÔNG khai báo INTERNET (ảnh không rời máy; bản Debug tự có INTERNET cho debugger).
- Màn hình Trang chủ hiển thị dòng "Lõi xử lý ảnh: Sauvola k = 0.34" để xác nhận ImageCore.Shared chạy được trên máy.
- Chạy trên máy: bật Tuỳ chọn nhà phát triển + Gỡ lỗi USB, cắm cáp, `adb devices` phải thấy máy, rồi
  `dotnet build DocScanner/DocScanner.csproj -f net10.0-android -t:Run`
  (adb: `C:/Program Files (x86)/Android/android-sdk/platform-tools`).
- Cần owner kiểm tra tay: mở app trên Note 10+, bấm "Chụp ảnh" (hộp thoại xin quyền camera hiện đúng, từ chối 2 lần
  thì có nút mở Cài đặt), bấm 3 nút chuyển màn hình, thấy dòng "Lõi xử lý ảnh".

### Bước 3: ghi chú
- Cấu trúc: `DocScanner.Core` (net9.0, thuần .NET, test được trên PC: `DocumentRecord` / `PageRecord`,
  `DocumentStore`, `ImportService`, `ImageGeometry`) + `DocScanner` (Android: `AndroidImageService`,
  `ImportCoordinator`, ViewModel, trang Home / Document). Test: `dotnet test DocScanner.Core.Tests` (24 test).
- (Đã thay đổi ở mục "Nhập nhanh" bên dưới: nhập ảnh chỉ chép bản gốc, phần còn lại chạy nền.)
- Lưu trữ: `files/documents/{docId}/doc.json` + mỗi trang một thư mục gồm `original.<ext>` (copy nguyên byte,
  không đụng EXIF), `proxy.jpg` (cạnh dài 1600, đã xoay đúng chiều) và `thumb.jpg` (320). `doc.json` ghi nguyên tử
  (file tạm + thay thế) sau MỖI trang nhập, nên crash / huỷ giữa chừng vẫn giữ các trang đã xong.
- Giải mã ảnh lớn: `BitmapFactory.inSampleSize` (co luỹ thừa 2 ngay trong decoder), rồi resize + xoay theo EXIF
  trên bitmap nhỏ; gặp `OutOfMemoryError` thì tự tăng sample và thử lại (tối đa 3 lần). PNG trong suốt được đặt
  lên nền trắng trước khi ghi JPEG. Bước 6 (cắt phối cảnh trên ảnh gốc) phải áp dụng đúng `ExifOrientation` đã
  lưu trong `PageRecord`.
- Trình chọn ảnh trên Samsung Android 12 là giao diện chọn tệp cũ: **bấm giữ** một ảnh rồi chạm thêm ảnh để chọn
  nhiều, sau đó bấm "Chọn" (Android 13+ dùng photo picker chuẩn). Nút Huỷ trên lớp phủ dừng ngay, giữ các trang đã nhập.
- Đã kiểm chứng trên Note 10+ (SM-N975F, Android 12): nhập 12 trang gồm WebP, JPEG 12 MP, JPEG 48 MP (8000x6000),
  EXIF 6 / 3 / 8 (cả ba dựng thẳng), PNG trong suốt; bản gốc lưu đúng byte; không crash; PSS ~314 MB khi đang xử lý.
- CHƯA thử trên máy: nút "Chụp ảnh" / "Chụp thêm" (camera hệ thống), xoá tài liệu bằng vuốt, xoá trang bằng nút x.
  Đường xử lý ảnh hỏng (bỏ qua ảnh lỗi, báo cuối đợt) mới được kiểm bằng unit test, vì trình chọn của hệ thống
  không trả về file giả.
- Nợ nhỏ: `doc.json` ghi tiếng Việt dạng escape unicode (hợp lệ, chỉ khó đọc); chưa đổi được tên tài liệu (Bước 8).

### Bước 4: ghi chú (dò mép giấy)
- Code: `ImageCore.Shared/DocumentEdgeDetector.cs` (+ `Geometry.cs` Quad, `Homography.cs`, `RgbImage.cs`,
  `IEdgeDetector.cs`). Thuần C#, không OpenCV. `DocScanner.Core/CropDetectionService.cs` chạy bộ dò trên proxy
  (cạnh dài 480) và ghi `CropQuad` (8 số 0..1, thứ tự TL TR BR BL, toạ độ ảnh đã xoay đúng chiều),
  `CropConfidence`, `CropDetected` vào `PageRecord`. `ImportService` gọi nó sau mỗi trang (lỗi dò không làm hỏng
  việc nhập). Trang Crop (`CropPage` + `QuadOverlay`) hiện khung màu xanh (dò được) hoặc cam (chỉ là toàn khung)
  kèm độ tin cậy và thời gian; chạm vào một trang trong tài liệu để mở. Trang cũ chưa có khung thì dò lúc mở.
- Thuật toán: Canny trên 3 kênh R/G/B (lấy kênh mạnh nhất, để giấy trắng trên bàn sáng vẫn thấy qua màu) ->
  Hough, mỗi điểm cạnh bỏ phiếu theo độ lớn gradient (cạnh giấy thật áp đảo vân bàn) -> tinh chỉnh từng đường bằng
  bình phương tối thiểu -> ghép cặp đường gần song song thành tứ giác lồi -> chấm điểm.
- Điểm tứ giác = phần trăm mỗi cạnh có điểm cạnh đúng hướng x tương phản trong/ngoài cạnh (cửa sổ 9x9, cách 7 px:
  loại dòng chữ giữa trang) x "bên ngoài không phải màu giấy" (loại lề trắng quanh khối chữ) x diện tích x độ vuông
  góc. Cạnh nằm trên khung ảnh (giấy tràn ra ngoài) được chấm bằng màu dải sát mép khung so với vùng trong tờ / màu bàn.
  Nhiều khung gần giống nhau thì chỉ đổi sang khung có mép mạnh gấp >= 2 lần (giấy vs vân bàn).
- Không dò được (điểm < 0,42) thì `Detected=false` và trả toàn khung lùi 3%.
- Kết quả đo trên ảnh giả lập (test `EdgeDetectorTests`, 19 cảnh + cảnh không có giấy): nền tối / sáng / xanh (chỉ khác
  màu), xoay 25 / 60 độ, phối cảnh gắt (trên hẹp 35%), tờ nhỏ (4% khung), tờ bị khung cắt, bóng đổ mềm, nhiễu mạnh, vân gỗ
  song song mép, tương phản 20 mức: đều IoU >= 0,92 (đa số >= 0,99). Cảnh chỉ có bàn + vật lạ: không báo có giấy.
  Tốc độ: khoảng 0,2-0,7 s cho ảnh 1600x1200 trên PC (chưa đo trên máy, dự kiến gấp 3-5 lần).
- CHƯA có ảnh chụp thật: mọi số liệu trên là ảnh giả lập tự sinh. Owner nên chụp 20-30 tờ thật (bàn tối / sáng /
  vân gỗ, nghiêng, tờ bị cắt, nhiều tờ chồng, có bàn tay giữ giấy) rồi xem khung trên trang Crop. Ca có thể còn yếu: giấy
  trắng trên bàn trắng, nhiều tờ chồng lên nhau, bàn tay che mép. Khi thấy sai, gửi ảnh + khung để chỉnh ngưỡng.
- Đã có sẵn `Homography` (giải 4 điểm, nghịch đảo) cho Bước 6 (cắt phối cảnh).

### Nhập nhanh (sau phản hồi của owner: spinner nhập ảnh quá lâu)
- Nhập ảnh giờ chỉ **chép bản gốc** và ghi trang ở trạng thái `Pending` (`ImportService`); UI vào ngay tài liệu, mỗi trang là
  một ô đang xoay. Phần nặng chạy nền trong `PageIngestQueue` (2 worker, ưu tiên nghiêm ngặt): (1) **thumbnail** cho mọi
  trang của lô (giải mã thu nhỏ mạnh, chục ms) -> `Preview`; (2) ảnh **proxy** 1600 px -> `Ready` (mở được); (3) **dò mép**.
  Ô trang hiện thumbnail ngay khi có; nhãn "Đang xử lý n ảnh..." hiện ở đầu tài liệu.
- Trạng thái lưu trong doc.json (`PageState`: Pending / Preview / Ready / Failed; tài liệu cũ không có trường này = Ready).
  Tắt app giữa chừng: lần mở sau `ResumePending()` làm tiếp (trang Pending làm lại, Preview làm nốt proxy, Ready chưa có khung thì dò).
  Ảnh không đọc được thành trang `Failed` (ô đỏ, chạm để xem lỗi, xoá được), không còn hộp thoại lỗi cuối đợt.
- `DocumentStore` giờ giữ **một bản DocumentRecord duy nhất** trong bộ nhớ (UI, nhập và nền dùng chung); mọi thay đổi qua
  `Update(docId, ...)` (khoá + lưu), đọc danh sách trang qua `Pages(docId)` (bản chụp). Không còn `Load` / `Save` công khai.
- Đo trên Note 10+ (26 ảnh: 20 ảnh 12 MP + 1 ảnh 48 MP + 5 ảnh chụp giả lập): mọi trang có thumbnail và mở được sau vài giây;
  dò mép chạy nền ~1,7 s/trang/worker (khoảng 25 s cho 43 trang) nên là phần chậm nhất, nhưng không chặn giao diện.
- Lỗi đã sửa: spinner nhập ảnh kẹt mãi. `Progress<T>` gửi callback muộn qua UI thread; sau khi nhập nhanh, callback cuối chạy
  SAU khi `IsBusy = false` và bật lại lớp phủ. Đã thêm cờ `_importing` chặn callback muộn (`ImportViewModelBase`).
- Ảnh thật đầu tiên của owner (tờ giấy cầm tay trên bàn gỗ, nền là màn hình + bàn phím): dò được khung, độ tin cậy 63%,
  nhưng cạnh phải và góc trên phải nằm hơi lệch vào trong tờ giấy (mất ~8% bề ngang). Ghi lại để chỉnh ở đợt sau.

### Bước 5: ghi chú (trình chỉnh 4 điểm)
- `Views/QuadEditor.cs` (GraphicsView tự vẽ ảnh + khung + tay cầm + kính lúp, dùng chung một hệ toạ độ) thay cho `QuadOverlay`.
  Kéo góc (chấm lớn); kéo ô vuông giữa cạnh = đẩy cả cạnh theo pháp tuyến; kéo trong khung = dời cả khung. Kính lúp 3x hiện phía
  trên ngón tay (xuống dưới khi gần mép trên), có chữ thập + khung vẽ bên trong. Khung không bao giờ thành lõm / tự cắt / quá
  nhỏ: bước kéo nào làm vậy thì không áp dụng (tay cầm dừng ở giới hạn). Màu: xanh lá = tự dò, cam = chỉ là toàn khung,
  xanh dương = đã chỉnh tay. Ngoài khung được làm tối. Lề 34 dp quanh ảnh để góc sát mép không dính cử chỉ "quay lại".
- Nút: **Tự động** (dò lại, ghi đè cả khung chỉnh tay), **Toàn ảnh**, **Xoay 90°**, **Xong**. Chỉ dùng được khi trang đã `Ready`.
- Lưu: thả tay là `PageEditService.SetCrop` (ghi doc.json ngay, `CropManual = true`). Dò tự động về sau KHÔNG ghi đè khung chỉnh
  tay (trừ nút Tự động); cũng bỏ kết quả dò nếu trang bị xoay trong lúc đang dò.
- Xoay: `PageRecord.UserRotation` (0/90/180/270 thuận chiều kim đồng hồ) cộng với EXIF của file thành `EffectiveOrientation`
  (`ImageGeometry.ComposeRotation`, dùng ma trận D4 cho 8 mã EXIF). File gốc không đổi. Xoay = khung xoay theo, trang về `Pending`,
  pipeline nền tạo lại thumbnail + proxy; khung cũ giữ nguyên, không dò lại. **Bước 6 phải dùng `EffectiveOrientation`.**
- Đo trên Note 10+ khi kéo góc 3 giây: 168 khung hình, trung vị 8 ms, p99 15 ms, 0,6% khung giật (gfxinfo).
- Đã thấy trên máy: khung chỉnh tay được lưu và khôi phục sau khi khởi động lại app; kính lúp đúng vị trí, kể cả khi chạm sát mép
  phải màn hình.
- Test: `DocScanner.Core.Tests` 56 test (xoay, ghép EXIF, xoay khung, ghi đè khung tay, đua giữa xoay và dò...).
- Lưu ý khi thử tự động bằng adb: nếu owner đang cầm máy hoặc chuyển sang app khác thì KHÔNG bấm giả lập, dễ chạm nhầm vào
  app khác (đã xảy ra một lần: chạm vào Thư viện của owner; không có thao tác nào được thực hiện).

### Bước 6: ghi chú (cắt phối cảnh)
- Thuật toán: `ImageCore.Shared/PerspectiveWarp.cs` (homography 4 điểm -> chữ nhật, nội suy song tuyến; khi ảnh nguồn dày hơn
  đầu ra > 1,25 lần thì lấy 2x2 điểm con rồi lấy trung bình để chữ nhỏ không bị răng cưa). Quy ước toạ độ "cạnh điểm ảnh"
  (điểm chuẩn hoá x rộng ảnh rơi đúng mép ảnh). Sai số trung bình 1,5-3 mức xám so với bản gốc trên ảnh mô phỏng có chữ.
- Luôn cắt từ **ảnh gốc**, không từ proxy: `CropRenderService` đổi khung từ toạ độ ảnh đã dựng thẳng về toạ độ file gốc bằng
  `ImageGeometry.UprightToStored` (ma trận trực giao của 8 mã EXIF, dùng `EffectiveOrientation` = EXIF + xoay của người dùng),
  chỉ giải mã vùng chứa tờ giấy (`BitmapRegionDecoder`, có dự phòng giải mã cả ảnh thu nhỏ cho định dạng không hỗ trợ).
- `CropPlanner`: đầu ra tối đa ~A4 300 DPI (cạnh dài 3508, 8,7 MP). Giải mã thu nhỏ luỹ thừa 2 lớn nhất mà vẫn đủ độ phân giải đầu ra
  (48 MP thường giải mã ở 1/2), và nếu vùng giải mã vượt 16 MP thì thu nhỏ thêm và đầu ra nhỏ theo. `android:largeHeap` bật.
- Kết quả lưu `cropped_{revision}.jpg` (JPEG q94) + `cropped_thumb_{revision}.jpg` (512 px) trong thư mục trang; tên theo revision để
  không dính cache hình cũ, file cũ xoá sau khi bản mới đã ghi vào doc.json. `PageRecord.NeedsRender` = chưa có bản dựng, hoặc khung /
  góc xoay đã đổi kể từ bản dựng gần nhất (lưu `CroppedQuad`, `CroppedRotation`).
- Luồng: trang Crop bấm **Xong** -> lưu khung (nếu chưa có) -> `ResultPage`; trang này xếp hàng `PageIngestQueue.EnqueueRender` (giai đoạn Render,
  ưu tiên cao nhất vì người dùng đang chờ) và tự cập nhật khi xong. Nút **Chỉnh lại** (về trang chỉnh) / **Xong** (về danh sách trang).
  Ô trang trong tài liệu hiện thumbnail đã cắt khi có bản dựng còn mới.
- Test: `DocScanner.Core.Tests` 86 test, gồm ánh xạ đủ 8 mã EXIF và 12 tổ hợp EXIF + xoay đầu-cuối (sai số < 1,5 mức xám; ánh xạ sai hướng
  sẽ lệch 40+), kế hoạch giải mã cho ảnh 12 / 48 MP / rất lớn, giai đoạn Render (bỏ qua khi không đổi, cũ bị thay, lỗi được ghi lại).
  `ImageCore.Shared.Tests` 44 test (thêm 8 test warp / resize).
- CHƯA làm: phóng to / kéo (pinch) trên trang kết quả; chưa thử ảnh gốc 48 MP thật trên máy; chưa đo thời gian dựng ảnh trên máy.
- Bước 7 (đen trắng) nên chạy trên chính `RgbImage` sau warp (gọi lại `CropRenderService` nhưng trả về ảnh trong RAM) để không nén JPEG hai lần,
  và nhớ giới hạn RAM của Sauvola (integral image `long`) cho ảnh 8,7 MP.


### Cập nhật sau Bước 6: kéo điểm ra ngoài ảnh
- Owner có tờ giấy bị khung ảnh cắt (trang 2 của tài liệu 15:10): góc thật của tờ giấy nằm ngoài ảnh. `QuadEditor` giờ cho kéo các
  điểm ra **vùng đen quanh ảnh**, tối đa `OutsideFraction = 0.2` (20% bề rộng / bề cao ảnh mỗi phía). Vùng đó được vẽ sáng hơn nền một chút,
  ảnh co lại còn ~71% để chừa chỗ. Toạ độ khung trong doc.json có thể nằm ngoài 0..1 (âm hoặc > 1); xoay / ánh xạ EXIF vẫn đúng.
- `PerspectiveWarp`: phần khung nằm ngoài ảnh gốc được điền **trắng** (giấy), hình học của khung vẫn nguyên; chỉ phần chưa từng chụp là trắng.
  Test: `Parts_of_the_outline_outside_the_photo...` (Shared) và `A_sheet_cut_by_the_frame...` (Core).
- Khung tự dò ở trang này sai (hình diều, tin cậy 50%): việc chỉnh bộ dò cho tờ giấy bị cắt mép vẫn còn tồn đọng.

### Cập nhật sau Bước 6: trang cắt ra khổ A4
- Mặc định trang sau khi cắt là **khổ A4** (tỉ lệ 1 : sqrt(2)), dọc hoặc ngang theo khung (rộng hơn cao = ngang). Cạnh dài giữ độ phân giải
  của khung (không thấp hơn cạnh dài của khung, cũng không thấp hơn cạnh ngắn x 1,414), rồi bị chặn ở 3508 px (A4 300 DPI, 3508 x 2480 ~ 8,7 MP).
  Code: `PerspectiveWarp.A4Size` (Shared), `CropAspect { A4, Free }` trong `CropPlanner`.
- Trang kết quả có nút **"Khổ giấy: A4 / theo khung"** đổi từng trang (biên lai, thẻ, khổ giấy khác): `PageRecord.FreeAspect`, và
  `CroppedFreeAspect` lưu khổ của bản dựng gần nhất để bản dựng cũ bị coi là cũ (`NeedsRender`). Đổi là tự dựng lại nền.
- Lưu ý: khổ A4 kéo giãn ảnh cho khớp tỉ lệ (khung gần vuông sẽ bị giãn chiều rộng); nếu tờ giấy không phải A4 thì chọn "theo khung".
- Test: `DocScanner.Core.Tests` 91 test (thêm khổ A4 dọc / ngang / 300 DPI / ảnh 48 MP, dựng A4 đầu-cuối và chuyển qua "theo khung"),
  `ImageCore.Shared.Tests` 46 test.

### Bước 7: ghi chú (đen trắng, đợt 2026-09-26)
- `PageRecord` có thêm `ColorMode` (Color / Gray / BlackWhite), `BwDarkness` (0..100, 50 = Sauvola k 0,34), `CleanBackground`; bản dựng ghi
  lại `Cropped*` tương ứng + `CroppedExtension` (".jpg" / ".png"). `NeedsRender` tính cả kiểu trang, nhưng chỉ những giá trị có ảnh hưởng
  (`PageRecord.SameLook`: độ đậm chỉ tính khi đen trắng, làm sạch nền chỉ tính khi không phải màu). doc.json cũ đọc ra là trang màu, không phải dựng lại.
- `CropRenderService`: warp -> `DocumentFilter.Apply` ngay trên `RgbImage` trong RAM (không đọc lại JPEG) -> lưu: màu / xám = JPEG q94
  (xám ghi dạng RGB vì Android không ghi được JPEG 1 kênh), đen trắng = **PNG 1-bit** do `PngWriter` tự viết (không cần codec nền tảng).
- `Binarizer.Sauvola` viết lại: tổng trượt theo cột + theo hàng, chạy song song theo dải; bộ nhớ O(rộng); kết quả **giống hệt từng điểm**
  bản integral cũ (test so với bản tham chiếu, nhiều kích thước). Cả bộ lọc đen trắng trên A4 300 DPI cấp phát ~34 MB (test `FilterMemoryTests`).
- `BackgroundFlattener`: ước lượng nền trên ảnh ~256 px (max filter + box blur), chia từng điểm cho nền; không làm sáng quá 1/0,3 lần mức giấy
  (bóng đổ thật có thể còn ~35% độ sáng). Dùng cho Xám và trước Sauvola.
- UI `ResultPage`: 3 nút kiểu trang, thanh trượt Độ đậm (`DragCompletedCommand`: chỉ dựng lại khi thả tay), công tắc Làm sạch nền, "Áp dụng
  kiểu này cho mọi trang" (`PageEditService.ApplyFilterToAll`, xếp hàng dựng lại các trang đổi kiểu). Ảnh cũ vẫn hiện trong lúc dựng lại.

### Bước 8: ghi chú (nhiều trang, PDF, chia sẻ)
- `ImagePdfWriter` (DocScanner.Core/Export): tự ghi PDF 1.7, ghi thẳng ra stream, mỗi lúc chỉ giữ 1 file trang trong RAM, **không giải mã ảnh**:
  JPEG nhúng nguyên byte (`/DCTDecode`, đọc kích thước / số kênh từ SOF), PNG 1-bit nhúng nguyên dữ liệu IDAT (`/FlateDecode` +
  `/Predictor 15`). Tiêu đề PDF UTF-16 (tiếng Việt). Không dùng PdfSharp trên điện thoại (tránh rủi ro trimming / phụ thuộc).
- `PdfExportService`: trang còn đang nhập hoặc chưa có bản dựng mới thì xếp hàng dựng qua `PageIngestQueue` (không tự dựng song song với
  hàng đợi -> không đụng số revision), chờ bằng polling 150 ms; trang lỗi bị bỏ và báo số trang. Khổ trang: A4 chuẩn 595x842 pt
  (theo chiều của bản dựng), trang "theo khung" giữ tỉ lệ với cạnh dài = A4. Ghi ra `.partial` rồi đổi tên.
- File xuất ở `FileSystem.CacheDirectory/exports/<tên tài liệu>.pdf` (`PdfExportService.FileNameFor`). Sau khi xuất: Chia sẻ
  (`Share.Default`), Lưu vào Tải xuống (`AndroidDownloadsService`, MediaStore.Downloads, Android 10+, ghi ở trạng thái pending rồi mới công bố),
  Mở (`Launcher`).
- `DocumentStore`: `Rename`, `MovePage`, `SetOrder` (hoàn tác chuyển trang), `TrashPage` / `RestorePage` / `EmptyTrash` (thư mục
  `{doc}/.trash`, hoàn tác 1 bước; thùng rác dọn khi thao tác mới, rời tài liệu, hoặc lúc mở app).
- UI `DocumentPage`: thanh tiêu đề "Xuất PDF" + menu "Đổi tên"; mỗi ô trang có nút ⋯ (mở, đưa lên đầu / trước / sau / cuối, xoá) và
  kéo thả (`DragGestureRecognizer` / `DropGestureRecognizer`: nhấn giữ một trang rồi thả lên trang khác); thanh "Đã xoá Trang n · Hoàn tác".
  Lớp phủ bận dùng chung (`ImportViewModelBase.RunBusyAsync`, nút Huỷ).
- Đã xoá trang giữ chỗ `ExportPage` / route `export`.
- Test: `DocScanner.Core.Tests` 103 (xuất PDF đọc lại bằng PdfPig: số trang, khổ A4 / theo khung, JPEG nguyên byte, PNG giải nén đúng,
  tiêu đề tiếng Việt, bỏ trang lỗi, huỷ không để lại file; đổi thứ tự / thùng rác / đổi tên). PDF thật (JPEG + PNG đen trắng) mở đúng bằng PDFium.

### Bước 8b: ghi chú (duyệt trang, menu, PDF đã xuất, icon; đợt 2026-09-26)
- Màn chỉnh khung (`CropPage`): hàng "‹ Trước · Kết quả · Sau ›" thay nút Xong ("Kết quả" = Xong cũ). `CropViewModel.Go(±1)` giữ lại khung đang
  hiện (`KeepShownOutline`, như Xong cũ) rồi đổi sang trang kề (`DocumentStore.Neighbor`) ngay trong cùng màn hình. `QuadEditor` có
  `SwipeCommand`: chạm bắt đầu **ngoài khung** (hoặc khi chưa có khung) rồi vuốt ngang >= 20% bề rộng, chủ yếu theo chiều ngang, thì chuyển trang;
  chạm trong khung vẫn là kéo khung / góc / cạnh.
- Màn kết quả (`ResultPage`): "‹ Trước · Chỉnh khung · Sau ›" + `SwipeGestureRecognizer` trái / phải trên ảnh. "Chỉnh khung" quay về bằng
  `..?docId=&pageId=` để màn chỉnh khung chuyển sang đúng trang đang xem.
- Xuất PDF: `Services/ExportCoordinator` (singleton, dùng chung cho Document / Crop / Result) + `Views/ExportOverlay` (tự bind vào singleton).
  PDF lưu ở `AppDataDirectory/exports` (`Core/Export/ExportLibrary`: tên "<tài liệu> yyyy-MM-dd HH.mm.pdf", thêm " (2)" nếu trùng; không còn dùng cache).
- Menu: `AppShell` bật flyout (header có `Resources/Images/scanner_logo.svg`): "Tài liệu" (Home), "PDF đã xuất" (`ExportsPage` /
  `ExportsViewModel`: danh sách mới nhất trước, chạm = Mở / Chia sẻ / Lưu vào Tải xuống / Xoá, vuốt = Xoá, kéo xuống = làm mới).
- Icon: `Resources/AppIcon/appicon.svg` (nền gradient xanh) + `appiconfg.svg` (tờ giấy + 4 góc khung + tia quét; nằm trong vùng an toàn
  của adaptive icon, `ForegroundScale="1"`), splash cùng hình, màu `#1A5FD6`.
- Test ổn định hoá: `FilterMemoryTests` đo cấp phát toàn tiến trình nên phải chạy riêng (collection `DisableParallelization`), trước đó
  thỉnh thoảng báo sai 80-126 MB do test khác chạy song song.

### Chất lượng PDF (đợt 2026-09-26, owner báo PDF 9 trang = 14 MB)
- Nguyên nhân (đo trên máy): trang màu nhúng nguyên bản dựng trong app, ~230-300 DPI, JPEG q94 = 1,25-2,2 MB / trang; trang đen trắng PNG 1-bit ~140 KB.
- Sửa: `Core/Export/PdfQuality` (Nhỏ 150 DPI / JPEG 70, **Vừa 200 DPI / JPEG 80 (mặc định)**, Cao 300 DPI / JPEG 90). Khi xuất, `PdfExportService`
  nén lại riêng các trang JPEG (`IImageService.LoadRgbAsync` với cạnh dài theo DPI, không phóng to; `SaveJpegAsync` theo chất lượng) vào thư mục tạm
  `.work_*` cạnh file PDF, xoá sau khi xong; trang PNG đen trắng nhúng nguyên. Bản dựng lưu trong app không đổi (vẫn q94).
- `ExportCoordinator` hỏi chất lượng mỗi lần xuất (action sheet, đánh dấu ✓ lựa chọn trước, nhớ bằng `Preferences` "pdf_quality").
- Ước lượng trên chính 8 trang màu của tài liệu đó (nén thử trên PC): hiện tại 13,2 MB -> Nhỏ 2,1 MB, Vừa 3,9 MB, Cao 10,7 MB (+0,14 MB trang đen trắng).

### Làm sạch nền màu: đã thử và BỎ (2026-09-26)
- Owner yêu cầu thử làm sạch nền cho trang màu để PDF nhỏ hơn. Đo trên 6 trang màu thật: không giảm (200 DPI / q72: 434 -> 448 KB/trang;
  nền trắng hẳn + xám cũng 440-454 KB). Dung lượng JPEG nằm ở nét chữ, không ở nền. Owner quyết định bỏ: trang Màu giữ nguyên ảnh,
  `BackgroundFlattener.WhitePoint = 1` (Xám / Đen trắng như cũ). Giữ `BackgroundFlattener.Flatten(RgbImage)` theo từng kênh (có test, chưa dùng).
- Giữ: JPEG khi xuất Nhỏ 150 DPI / q60, Vừa 200 DPI / q72, Cao 300 DPI / q90.
- Cách thật sự giảm dung lượng trang chữ: **Đen trắng** (đo: 152 KB/trang ở độ phân giải đầy đủ, 6 trang = 0,9 MB).

### Trang 7 bị méo khi xuất (2026-09-26)
- Owner báo trang 7 "không đúng A4, gần vuông". Dữ liệu thật: tờ giấy bị khung ảnh cắt, khung chỉnh tay có 1 góc ra ngoài ảnh -> hình thang
  rất lệch (cạnh 1349 / 2576 x 2378 / 2329 px). `PerspectiveWarp.A4Size` chọn dọc / ngang theo cạnh DÀI NHẤT mỗi cặp -> chọn A4 ngang
  (3344x2365) trong khi dáng trung bình là dọc 1 : 1,2 -> chữ bị kéo giãn ngang 1,7 lần.
- Sửa: (1) dọc / ngang theo dáng trung bình (`PerspectiveWarp.IsLandscapeShape`); (2) khung không có dáng tờ giấy (tỉ lệ ngoài 1,15-1,75:
  `IsA4Like`) giữ tỉ lệ thật dù đang ở chế độ A4 (`CropPlanner`), khi xuất PDF được đặt giữa trang A4 có lề trắng (`PdfExportService.PageLayout`,
  `PdfPageSource.ImageRect`); (3) bản dựng cũ bị méo tự dựng lại (`PageRecord.RenderStretchedToA4` trong `NeedsRender`, chỉ ảnh hưởng trang méo).
- Kiểm chứng: test với đúng số liệu trang 7; dựng lại trang 7 thật trên PC từ ảnh gốc -> A4 dọc 2481x3508, chữ đúng tỉ lệ (bản sao đã xoá).

### Tối ưu tốc độ (đợt 2026-09-26, chi tiết: MOBILE-STATUS.md mục 5b)
- Công cụ đo: `Source/MobileBench` (chỉ có trong monorepo cũ, KHÔNG có trong repo này; không nằm trong sln; `dotnet run -c Release --project Source/MobileBench [số lần]`,
  `... -- detect-dump <file>` để so kết quả bộ dò trước / sau khi sửa, `... -- scene-bmp <file>` tạo ảnh chụp giả lập 12 MP).
- Dò mép nhanh ~3,8 lần, kết quả giống hệt từng bit; `ToGray` / `FromGray` / `Resize` / làm phẳng nền nhanh hơn, kết quả giống hệt.
- `PageIngestQueue.Prerender`: dựng trang ở nền sau khi dò mép (1 luồng, ưu tiên thấp nhất); `EnqueueRender` vượt hàng, không dựng trùng một trang.
  Màn kết quả / xuất PDF dùng `IsPreparing` thay cho `IsBusy`.
- Android: `BitmapPixels` (AndroidBitmap_lockPixels) thay GetPixels / CreateBitmap(int[]); JPEG ghi thẳng FileOutputStream Java.
- Đã chạy trên máy ảo Android (Debug): dựng nền 3 trang đúng, màu đúng kênh. **Chưa đo trên Note 10+ bản Release.**
- Mẹo adb trong Git Bash: đặt `MSYS_NO_PATHCONV=1`, nếu không `/data/local/tmp` bị đổi thành đường dẫn Windows.

### Nhập ảnh chạy nền (đợt 2026-09-27, chi tiết: MOBILE-STATUS.md mục 5c)
- `BackgroundImporter` (Core, singleton) thay spinner nhập ảnh: `Start` trả về ngay, 1 worker chép lần lượt, trạng thái theo tài liệu
  (`Status` / `Changed` / `Stop` / `Dismiss`). Màn tài liệu thêm ô trang dần + dòng "Đang nhập x/y" + Dừng; Home hiện "đang nhập x/y".
- `AndroidPhotoPicker` thay `MediaPicker.PickPhotosAsync` (MAUI chép mọi ảnh vào cache trên luồng UI -> treo với 100 ảnh). Camera vẫn dùng MediaPicker.
- Đã xoá `BusyOverlay` và `ImportViewModelBase`. 125 test PASS. Bản Debug đã cài lên Note 10+ (06:30 27/09), chưa bấm thử trên máy.

### Ô "Đang tải..." + xem trước tức thì (đợt 2026-09-27b, chi tiết: MOBILE-STATUS.md mục 5d)
- `PageState.Importing` = ô chờ: `ImportService.AddPlaceholders` / `FillAsync` (điền tại chỗ, lỗi -> `Failed` tại chỗ); dọn ô sót khi mở app
  (`DocumentStore.RemoveUnfinishedImports`). Tiến trình từng ô: `BackgroundImporter.PageChanged` / `CopyProgress`. `DocumentViewModel` chỉ làm mới đúng ô.
- Màn kết quả xem trên bản nắn cỡ màn hình trong RAM (`CropRenderService.RenderPreviewAsync`, 1800 px); đổi kiểu lọc lại bản nhỏ; độ sáng / tương phản
  = `ToneAdjust` (ColorMatrix khi xem, LUT khi lưu, cùng công thức). Bản đầy đủ lưu nền 0,8 s sau lần chỉnh cuối.
- 133 + 78 test PASS, SmokeTests ALL PASS. Bản Debug đã cài Note 10+ (07:19 27/09), chưa bấm thử.
- Xoay ở màn kết quả = `PageRecord.OutputRotation` (xoay trang đã nắn, không đụng ảnh gốc / khung; `PageEditService.RotateOutput`); bản xem trước xoay
  trong RAM, bản đầy đủ nắn -> xoay -> lọc. 134 + 80 test PASS; cài Note 10+ 07:32 27/09.

### Giao diện kiểu TapScanner (đợt 2026-09-27c, chi tiết: MOBILE-STATUS.md mục 5e)
- Font biểu tượng Material Icons ("Icons", `Views/Icons.cs`), control `Views/ToolButton.cs` (biểu tượng + chữ nhỏ). Màu thương hiệu #1A5FD6.
- Không còn menu trượt; thanh dưới ở mọi màn; màn kết quả có bảng Bộ lọc / Điều chỉnh (một bảng mỗi lúc), nút Xong ✓.
- Thử trên máy ảo Pixel 7 API 36 (`emulator -avd pixel_7_-_api_36_0`): 4 màn đúng, luồng lọc / xoay / Xong chạy. Chưa cài Note 10+.

### So với TapScanner + tốc độ bản Release (đợt 2026-09-27d, chi tiết: MOBILE-STATUS.md mục 5f)
- `Perf` (Core) ghi thời gian các khâu ra logcat tag `DocScanPerf`; đo trên máy thật bằng `adb logcat -s DocScanPerf` (thụ động).
- Release: AOT toàn bộ + LLVM (csproj). Chép ảnh qua file descriptor; bỏ copy bitmap thừa; mở màn kết quả dùng lại bản dựng Màu (`HasPlainColorRender`).
- Thẻ bộ lọc có ảnh xem trước của trang. Còn thiếu so với TapScanner: camera trong app (chụp liên tục, dò mép trực tiếp), tìm kiếm / thư mục, chọn nhiều trang.
- Lưu ý đo: bản Release không `run-as` được; máy ảo hay hiện "System UI isn't responding" (bấm Wait ở 322,1368).

### Mượt khi chỉnh ảnh (đợt 2026-09-27e, chi tiết: MOBILE-STATUS.md mục 5g)
- **Đánh giá tốc độ bằng bản Release.** Debug của MAUI mặc định chạy trình thông dịch (chậm 10-30 lần); csproj đã đặt Debug `UseInterpreter=false`.
- `LookPreview` (pipeline xem trước có bộ nhớ đệm), `ParallelScope` (giới hạn lõi cho việc nền), dựng lỗi thời tự huỷ, xoay bằng hoán vị + hiệu ứng GPU,
  bitmap dùng lại. Đen trắng có Độ sáng (`Binarizer.Sauvola(..., offset)`).
- Release đã cài Note 10+ 11:04 27/09 (owner yêu cầu). Log thời gian: `adb logcat -s DocScanPerf`.

### Camera trong app + nắn thẳng đúng tỉ lệ (đợt 2026-09-27f, chi tiết: MOBILE-STATUS.md mục 5h)
- Crash camera: `MediaPicker.CapturePhotoAsync` đòi WRITE_EXTERNAL_STORAGE trên Android 12 -> `AndroidPhotoCapture` (ACTION_IMAGE_CAPTURE + FileProvider),
  giờ chỉ là dự phòng.
- Tỉ lệ thật: `PageGeometry` (Zhang & He 2007). Cạnh cong: `PageOutlineRefiner` + `PageBends` (`PageRecord.CropBend`), `PerspectiveWarp` nắn cong;
  `PageRecord.GeometryVersion` (bản dựng cũ tự dựng lại). `QuadEditor.Bend` vẽ cạnh cong.
- Camera: `Platforms/Android/Camera/DocumentCameraActivity` (CameraX 1.6.2), `DocumentEdgeDetector.Live()` trên khung 320 px, tự chụp
  `DocScanner.Core/Camera/CaptureStabilizer`. Mã request Activity: picker 0x5043, camera hệ thống 0x5044, camera trong app 0x5045 (trùng mã = kết quả bị nuốt).
- Bộ dò dùng lại mảng lớn theo luồng (`DocumentEdgeDetector.Scratch`): GC mảng lớn trên Android dừng cả Java. Kết quả giống hệt (detect-dump).
- Máy ảo có camera cảnh 3D (`hw.camera.back=virtualscene`) để thử camera khi không có máy thật.
- Bản Release (có camera mới) đã cài Note 10+ lúc 13:51 27/09 theo yêu cầu owner; camera mới chưa được thử trên máy.

### Đen trắng sắc nét, xem ảnh / PDF, chữ ký (đợt 2026-09-27g, chi tiết: MOBILE-STATUS.md mục 5i)
- Đen trắng: `Sharpen.UnsharpInPlace` trước Sauvola + `Binarizer.Shade(ramp)` mép mềm (`DocumentFilter.SmoothRamp = 12`); trang lưu PNG xám 8-bit,
  PDF Nhỏ / Vừa tự chuyển 1-bit (`PdfExportService.BilevelAsync`), Cao giữ nguyên. `PageRecord.BlackWhiteVersion` = 2 (trang đen trắng cũ dựng lại).
  Đo bằng `Tools/EdgeProbe bw <thư mục>` (giả lập ảnh điện thoại chụp trang chữ tiếng Việt).
- Zoom: `Platforms/Android/ZoomController` (ma trận ImageView) + `ZoomImageHost`. Trình xem: `ViewerPage` (route viewer), `PdfViewerPage` (pdfviewer, PdfRenderer).
- Chữ ký: `Core/Signatures` (`SignatureInk`, `SignatureLibrary`, `Stamper`), `PageRecord.Stamps` (`PageStamp`), `SignaturePage` + `StampEditor` / `SignaturePad`,
  `SignatureSession` chuyển bản xem trước từ màn Sửa. Chữ ký số bằng chứng thư (PAdES) CHƯA làm.
- Release đã cài Note 10+ lúc 19:54 27/09.
- Lưu ý khi sửa file có tiếng Việt: script PowerShell 5 không BOM đọc theo ANSI và làm hỏng chữ; dùng công cụ Edit (hoặc script có BOM).

### Sửa PDF / A4, thư mục, tìm kiếm, Cài đặt, tự cập nhật, splash (đợt 2026-09-27h, chi tiết: MOBILE-STATUS.md mục 5j)
- PdfRenderer: phải gọi `page.Close()` (Dispose của .NET không đóng trang Java) -> trước đây trang 2+ bị đen.
- Tỉ lệ trang: `PageGeometry.OutputAspect` dùng tiêu cự cố định 0,62 x đường chéo, giới hạn bù ±20%, trang có dáng tờ giấy -> A4. KHÔNG dùng
  tiêu cự đo từ 4 góc (`MeasureFocal` chỉ để chẩn đoán). `GeometryVersion = 3`.
- Màn chính: `HomeViewModel` (thư mục `DocumentStore.Folders / MoveToFolder`, `TextSearch`, chọn nhiều, kéo thả `DragGestureRecognizer` -> thư mục),
  route `folder` dùng lại `HomePage`. Cài đặt `SettingsPage`, `AboutPage` (tác giả, email, SĐT, điều khoản).
- Tự cập nhật: `AndroidAppUpdater` + `UpdateJobService` (JobScheduler id 21840, 01:00) + `UpdateStatusReceiver`; cần quyền
  `UPDATE_PACKAGES_WITHOUT_USER_ACTION` để cài im lặng. Đừng gọi `JobScheduler.schedule` cùng id khi job đang chạy (nó dừng job) -> `Schedule(replace)`.
  Thử: `adb reverse tcp:8080 tcp:8080`, `adb shell cmd jobscheduler run -f btk.docscanner 21840` (không force-stop app: force-stop xoá job).
  Đã kiểm chứng trên máy ảo: tự cài 1.1 -> 1.2 -> 1.3, lần job 01:00 cài im lặng khi app ở nền.
- Script PowerShell có tiếng Việt: ghi file .ps1 kèm BOM hoặc dùng Edit; GNU sed hiểu `\u` là viết hoa (đừng dùng sed để ghi `\uXXXX`).

### Dò mép bám sát + căn chữ thẳng hàng (đợt 2026-09-28b, chi tiết: MOBILE-STATUS.md mục 5l)
- Xem khung dò trên ảnh thật: chép thư mục tài liệu từ máy (bản Debug: `run-as`), chạy
  `dotnet run -c Release --project Source/Tools/EdgeProbe -- sheet <thư mục> <out.png>` (`CELL=700`, `TRACE=1`, `PAGE_OUT=<thư mục>`;
  `Tools/EdgeProbe` chỉ có trong monorepo cũ, KHÔNG có trong repo này).
  Xem ảnh xong thì xoá bản sao (ảnh cá nhân của owner).
- `PageOutlineRefiner`: điểm dốc nhất của mép, đường đồng thuận + Tukey, neo góc bộ dò khi bằng chứng ra khỏi ảnh, `KeepInPicture` 1%.
  `DocumentEdgeDetector`: thử góc ngoài khung tới 20% chỉ khi lượt thường thất bại. `ContentAligner` chạy sau nắn trong `CropRenderService`.
- Đổi luật dò thì tăng `PageRecord.DetectionVersion` (khung tự động cũ được dò lại một lần); đổi luật nắn / dựng thì tăng `GeometryVersion`.
- GDI+ thu nhỏ ảnh phải `ImageAttributes.SetWrapMode(TileFlipXY)`, nếu không viền tối 1 px bị bộ dò coi là mép giấy.

### Chữ đen trắng "vỡ" khi phóng to (đợt 2026-09-28c, chi tiết: MOBILE-STATUS.md mục 5m)
- Xem trước/sau bằng `dotnet run -c Release --project Source/Tools/EdgeProbe -- bw <thư mục>`: ảnh trang chữ giả lập chụp bằng điện thoại,
  nhị phân hoá bằng đúng các bước app dùng, xuất crop 100% / cỡ màn hình / **phóng to 4x song tuyến** (đúng cách ImageView Android phóng ảnh)
  và số liệu KB / % lệch so với bản gốc -- không cần ảnh thật của owner để so sánh chất lượng đen trắng.
- Nguyên nhân: `PdfExportService` nhị phân hoá cứng (1-bit) trang đen trắng cho cả **Nhỏ và Vừa** (mặc định) khi xuất PDF -- ảnh lưu trong app
  vốn đã mượt viền (8-bit), chỉ mất mượt lúc xuất. Đã thử siêu lấy mẫu (supersample) để mượt hơn nữa nhưng không rõ rệt và tốn nét mảnh, bỏ.
- Sửa: `PdfQuality` thêm `GrayDpi` (null = nhị phân hoá như cũ). **Vừa**: `GrayDpi = 150`, thu nhỏ bằng `GrayImage.Resize` (mới, giống
  `RgbImage.Resize`) nhưng giữ 8-bit mượt -- đổi lại trang đen trắng to hơn ~1,9 lần (350-400 KB thay vì 150-200 KB). **Nhỏ**: không đổi
  (`GrayDpi = null`), vẫn 1-bit cho size bé nhất. **Cao**: không đổi (giữ nguyên file, đã mượt).

### Xuất PDF tự động lưu vào Tải xuống/DocScanner (đợt 2026-09-28d, chi tiết: MOBILE-STATUS.md mục 5n)
- `ExportCoordinator.ExportAsync` gọi `IDownloadsService.SaveAsync` ngay sau khi xuất xong (không chờ người dùng bấm "Lưu vào Tải xuống"); lỗi
  lưu không làm hỏng lần xuất (file thư viện riêng của app vẫn còn, chỉ báo lỗi + vẫn có nút lưu tay).
- `AndroidDownloadsService`: `RelativePath = Download/DocScanner` (hằng số `Subfolder`) thay vì `Download` -- MediaStore tự tạo thư mục con.
- Chưa unit-test được (tầng Android MAUI); build Debug qua kiểm tra, cần owner xuất thử 1 tài liệu và xem `Tải xuống/DocScanner` trên máy.

### Trang mới nhập mặc định đen trắng (đợt 2026-09-29, chi tiết: MOBILE-STATUS.md mục 5o)
- Gán `ColorMode = PageColorMode.BlackWhite` ngay khi tạo `PageRecord` mới, trong `ImportService.AddPlaceholders` (nơi
  duy nhất tạo trang mới) -- KHÔNG đổi default của chính thuộc tính `PageRecord.ColorMode` (vẫn `Color`), vì default đó
  còn phục vụ việc đọc đúng `doc.json` cũ thiếu trường này (trang màu đã render sẵn, xem test
  `A_document_saved_before_page_looks_existed_still_reads_as_rendered_color_pages`) -- đổi default ở đó sẽ làm tài liệu
  cũ dạng này tự nhận nhầm "đã đúng đen trắng" mà không dựng lại.
- 4 test cũ ngầm dựa vào "trang mới nhập là màu" (kiểm tra hành vi trang màu, không phải kiểm tra mặc định nhập ảnh)
  được sửa để tự đặt `PageColorMode.Color` ngay sau khi nhập trong rig test. Test mới:
  `A_newly_imported_page_defaults_to_black_and_white`. 194 test PASS.

### Dòng chữ / mép trang gợn sóng (đợt 2026-09-29, chi tiết: MOBILE-STATUS.md mục 5q)
- Owner báo trực tiếp trên máy (tài liệu T1, ảnh chụp camera): "một số trang các dòng chữ như bị gợn sóng." Chẩn đoán bằng chạm/vuốt +
  screenshot trong app (bản Release, không `run-as` được) -- trang 2 có mép dưới tờ giấy (giáp nền đen) gợn sóng rõ.
- Nguyên nhân: `ContentAligner` (đợt 2026-09-28b) khi đo được cả 4 dải rõ nét thì xoay TỪNG HÀNG ảnh theo góc nội suy tuyến tính từng đoạn
  giữa 4 điểm đo (piecewise), không phải một đường thẳng chung -- tính năng này từng sửa đúng một trang thật khác (mép bị khuất, đường thẳng
  chung bỏ sót một dải), nhưng trên trang T1 này 2 dải liền kề lệch nhau chỉ do nhiễu đo, và nội suy đúng qua điểm nhiễu biến mép thẳng thành
  sóng.
- Sửa: bỏ hẳn nội suy piecewise. `LineTilt.At(v)` chỉ còn `Angle + Slope * (v - 0.5)` (affine) -- không thể tạo hơn một đoạn cong, không bao giờ
  ra sóng dù Angle/Slope thế nào. `PageRecord.GeometryVersion` tăng lên 5 (trang cũ dựng lại một lần ở nền).
- Test: `ImageCore.Shared.Tests` 135 (thêm `A_straight_edge_never_comes_out_wavy`, kiểm trực tiếp ở tầng hình học: mép thẳng qua `Align` với
  nhiều Angle/Slope không đổi hướng quá 1 lần). `DocScanner.Core.Tests` 199 không đổi cách khác.

### Cài đặt: bỏ tự cập nhật, mô tả Pro, ký AAB (đợt 2026-09-29 tối, chi tiết: MOBILE-STATUS.md mục 5r)
- Owner yêu cầu viết lại màn Cài đặt: ẩn phần cập nhật khỏi khách hàng (chỉ hiện version), đồng bộ version ở Thông tin ứng dụng, thêm mô tả lợi
  ích Pro, rồi xuất AAB + cài máy thật.
- Quyết định tự đưa ra: **gỡ bỏ hẳn** tính năng tự cập nhật (không chỉ ẩn UI) -- `AndroidAppUpdater`/`UpdateJobService`/`UpdateStatusReceiver`/
  `IAppUpdater`/`UpdateManifest`/`UpdatePlanner`/`UPDATE-SERVER.md` xoá hết, cùng 4 quyền Android không còn cần (`UPDATE_PACKAGES_WITHOUT_USER_ACTION`,
  `REQUEST_INSTALL_PACKAGES`, `POST_NOTIFICATIONS`, `RECEIVE_BOOT_COMPLETED`). Lý do: `UnsupportedReason` vốn đã tự tắt tính năng này khi cài từ
  Play (mọi khách hàng thật), nên giữ code chỉ là nợ kỹ thuật; đây cũng đúng là nguồn gốc câu hỏi "chưa biết cập nhật xong upload vào đâu" của
  owner. Cài Cài đặt còn lại mục "PHIÊN BẢN" (chỉ 1 dòng version) + khối lợi ích Pro dưới nút Mua Pro (IsVisible khi còn miễn phí).
- Phát hiện khi bắt đầu (git diff, không phải tôi tạo): owner đã tự điền mã AdMob thật + đổi `LicenseService.ProProductId` sang ID Play Console
  thật trước khi giao việc này -- `AdsConfig.HasRealIds` giờ true nên bản Release hiện quảng cáo thật, cẩn thận không bấm quảng cáo trên máy test
  chưa đăng ký Test device.
- `versionName` 1.1 -> 1.2, `versionCode` 2 -> 3. Tạo keystore upload key lần đầu (`DocScanner/release/`, gitignore, xem
  `KEYSTORE-README.md`) -- mật khẩu KHÔNG đặt trong `DocScanner.csproj` (file này nằm trong git) mà tách `release/Signing.props`, chỉ import khi
  file tồn tại.
- Build Release qua (188 test `DocScanner.Core.Tests` + 135 test `ImageCore.Shared.Tests` PASS), APK ký đúng keystore thật (`apksigner verify`).
  Lần cài đầu bị Android từ chối (`INSTALL_FAILED_UPDATE_INCOMPATIBLE`, khoá mới khác khoá cài trước đó) -- **không tự gỡ bản cũ** vì sẽ mất tài
  liệu owner đang test trên máy, dừng lại chờ owner. Owner tự gỡ app, báo lại; cài bản khoá thật thành công (máy hiện versionCode=3/versionName=1.2).
  Xuất AAB `DocScanner/release/DocScanner-1.2-versionCode3.aab` (50,5 MB, `jarsigner -verify` xác nhận hợp lệ) -- file owner tải lên Play.
  Chi tiết đầy đủ: MOBILE-STATUS.md mục 5r.

### Quảng cáo toàn màn hình, nhập PDF, chữ ký từ ảnh có sẵn (đợt 2026-10-04)
- Owner đã tự đổi mô hình kiếm tiền trước khi giao việc này: bỏ paywall, xuất PDF lần thứ 5 mới bật quảng cáo 1 lần (`AdsPolicy.ExportsPerInterstitial = 5`,
  code này owner tự sửa, không phải tôi).
- **Banner quảng cáo toàn bộ trang** (trước chỉ ở Home): `Views/AdBannerView.xaml(.cs)` -- ContentView tự lấy `IAdsService` qua
  `IPlatformApplication.Current.Services` (không cần ViewModel nào biết tới quảng cáo), tự ẩn/hiện theo `ShowAds`. Gắn vào cả 10 trang (trừ
  `SplashPage`): Home, Document, Crop, Result, Exports, Signature, Settings, About, PdfViewer, Viewer. Không có "màn Camera" hay "màn Import"
  dạng trang MAUI để loại trừ riêng -- camera là Activity Android gốc, nhập ảnh là system photo picker, cả hai che kín màn hình nên banner của
  trang bên dưới vốn không hiện ra được trong lúc đó; `AdBannerView` phủ mọi trang còn lại là đã đúng "trừ khi mở camera/import ảnh" theo nghĩa thực tế.
  `HomeViewModel` bỏ hẳn `ShowAds`/`ads.Changed` (không cần nữa, ContentView tự lo). Sửa lại comment `IAdsService.ShowAds` (cũ nói sai "export
  screens have no banner") và dòng quyền riêng tư lỗi thời trong AboutPage còn nhắc "tự cập nhật" đã bị gỡ bỏ từ đợt 2026-09-29.
- **Nhập PDF, tách trang vào batch**: `IPdfPicker`/`AndroidPdfPicker` (Platforms/Android) -- `ACTION_GET_CONTENT` chọn 1 file PDF, rồi dùng
  `Android.Graphics.Pdf.PdfRenderer` có sẵn trong Android (giống hệt cách `PdfPages` đã dùng để xem PDF, không thư viện, không rủi ro bằng sáng
  chế) vẽ từng trang ra JPEG trong cache (3508 px cạnh dài = A4 300 DPI, khớp mức "Cao" khi xuất PDF). Mỗi trang PDF trở thành một `ImportSource`
  bình thường, đưa thẳng qua `BackgroundImporter`/`ImportService` y hệt ảnh chụp/thư viện -- không viết pipeline riêng, nên crop tự động, đen
  trắng, quản lý trang... đều hoạt động với trang nhập từ PDF như mọi trang khác. Nút "Nhập PDF" ở toolbar Home (tài liệu mới) và Document
  (thêm vào tài liệu đang mở), lệnh `ImportCoordinator.FromPdfAsync` giống hệt `FromGalleryAsync`/`FromCameraAsync`.
- **Chọn chữ ký có sẵn trong máy**: `DocScanner.Core.Signatures.SignatureImageImport.ToMask(RgbImage)` -- chuyển ảnh chụp/ảnh chữ ký thành mask
  mực 0=giấy/255=mực, dùng lại `Binarizer.OtsuThreshold` (thuật toán tự viết sẵn có, không thêm rủi ro license/bằng sáng chế), ngưỡng mềm (ramp)
  chỉ ở phía trên threshold để giữ đúng quy ước "<=threshold là mực" của `Binarizer.Threshold`, tránh mất nét với ảnh ít nhiễu (bắt được bằng unit
  test tự viết, xem bên dưới). `SignatureViewModel.SaveFromImage` lưu qua đúng `SignatureLibrary.Add` như chữ ký vẽ tay. Nút "Chọn ảnh" (thẻ nét
  đứt, cạnh "Vẽ chữ ký") trong `SignaturePage`, code-behind `OnPickImage` dùng `MediaPicker.Default.PickPhotoAsync()` (API cũ còn cảnh báo
  Obsolete, gợi ý đổi `PickPhotosAsync` -- chưa đổi, không ảnh hưởng chức năng).
- Test: `DocScanner.Core.Tests` 189 PASS (thêm `A_picked_photo_becomes_an_ink_mask_dark_pixels_become_ink`; phát hiện ngay bug đầu tiên của
  `ToMask` -- công thức mềm hai phía làm ảnh nhị phân sạch/không nhiễu bị mất gần hết mực vì ngưỡng Otsu trùng đúng giá trị mực, sửa lại công
  thức rồi test mới pass). `dotnet build DocScanner.csproj -f net10.0-android`: BUILD SUCCEEDED, 0 lỗi (34 warning, toàn bộ có từ trước + 1
  warning Obsolete mới nêu trên).
- **Owner cần kiểm tra tay** (chưa có máy thật/máy ảo trong phiên làm việc này): banner hiện đúng trên từng trang kể trên; nhập PDF (nhất là PDF
  nhiều trang, PDF scan nặng, PDF có mật khẩu -- `PdfRenderer` sẽ ném lỗi, hiện đã bắt bằng try/catch báo "Không đọc được file PDF" nhưng chưa thử
  thật); chọn ảnh chữ ký từ thư viện (ảnh ít tương phản, nền không trắng tinh, ảnh đã có nền trong suốt).
- Chưa làm (ngoài phạm vi yêu cầu, chỉ ghi nhận): dọn dẹp cache `pdfimport_*.jpg`/`signature_pick_*.jpg` nếu app bị kill giữa chừng trước khi
  `FileOptions.DeleteOnClose` kịp chạy (rò rỉ vài trăm KB/lần lỗi, không tích luỹ lớn vì tên file có GUID nên không đụng lẫn nhau; dọn theo tuổi
  file lúc khởi động là việc hợp lý cho đợt sau nếu owner thấy cache phình to).

#### Phương án tự động cập nhật / bắt buộc cập nhật (đưa ra, CHƯA triển khai -- owner chọn hướng rồi làm tiếp)
Bối cảnh: đợt 2026-09-29 đã **chủ động gỡ bỏ hẳn** bộ tự cập nhật tự viết (`AndroidAppUpdater`/`UpdateJobService`...) vì `UnsupportedReason` đã tự
tắt nó khi cài từ Play (mọi khách hàng thật), nên giữ lại chỉ là nợ kỹ thuật không ai dùng tới. Ba phương án cho yêu cầu lần này:

1. **Dựa vào tự động cập nhật của Google Play (khuyên dùng, không cần viết code)**: Play tự cập nhật app lên bản mới khi có Wi-Fi + sạc (mặc định
   BẬT với đa số người dùng, họ tự tắt được trong cài đặt Play Store của máy họ). Không cần động vào code DocScanner. Nhược điểm: không ép buộc
   được -- người dùng tắt tự động cập nhật, hoặc không mở Play Store lâu ngày, vẫn dùng bản cũ vô thời hạn; không có cách nào từ phía app "giục"
   họ cập nhật.
2. **"Nên cập nhật" mềm, dùng Play In-App Update API (Flexible flow)**: thêm gói `Xamarin.Google.Android.Play.AppUpdate` (Apache-2.0, chính chủ
   Google, KHÔNG phải tự viết lại updater như bản đã gỡ) gọi `AppUpdateManager.StartUpdateFlowForResult` kiểu Flexible -- Play tự tải bản mới
   nền, xong hiện thanh "Đã có bản mới, bấm để cài lại" ở cuối màn hình, người dùng bấm lúc nào tuỳ ý, không chặn dùng app. Cần: app đã đăng ký
   theo dõi bản cập nhật trên Play Console (production track), đo được `UpdateAvailability`/`stalenessDays` để tự quyết lúc nào nhắc.
3. **Bắt buộc cập nhật mới dùng tiếp, dùng Play In-App Update API (Immediate flow)**: cùng gói như trên nhưng gọi kiểu Immediate -- Play hiện màn
   toàn màn hình "Cần cập nhật để tiếp tục", chặn thao tác cho tới khi cài xong bản mới; `AppUpdateManager` báo `UpdateAvailability` +
   `IsUpdateTypeAllowed(Immediate)` để app tự quyết khi nào bắt buộc (ví dụ: bản hiện tại cũ hơn X ngày, hoặc owner đánh dấu "bản này có lỗi
   nghiêm trọng, ép cập nhật" qua một cờ nhỏ, ví dụ Remote Config/metadata JSON tĩnh host ở đâu đó -- quay lại đúng câu hỏi "chưa biết tải lên
   đâu" mà đợt 2026-09-29 đã né bằng cách gỡ bỏ cả tính năng; nếu chọn phương án 3 thì CẦN giải quyết lại chỗ này, ví dụ dùng chính Play Console
   "staged rollout"/"in-app update priority" (đặt priority 0-5 cho mỗi bản, không cần hạ tầng riêng) thay vì tự host file cấu hình).
- **Khuyến nghị**: phương án 2 (Flexible) cho trải nghiệm tốt nhất với chi phí thấp nhất (không hạ tầng riêng, dùng đúng API Google thiết kế cho
  việc này); phương án 3 (Immediate) chỉ nên bật cho các bản sửa lỗi nghiêm trọng/bảo mật, dùng `UpdatePriority` đặt trong Play Console (0-5, app
  đọc qua `AppUpdateInfo.UpdatePriority()`) để tự quyết Flexible hay Immediate theo từng bản, không cần máy chủ cấu hình riêng.
- **Owner chọn hướng 3, áp dụng cho MỌI bản (không phân biệt ưu tiên, không dùng `UpdatePriority`)** -- đã triển khai cùng đợt, xem mục ngay dưới.

### Bắt buộc cập nhật qua Play In-App Update, không server riêng (đợt 2026-10-04, tiếp ngay sau mục trên)
- `Platforms/Android/AndroidAppUpdate.cs` (`AndroidAppUpdate.CheckAndForce(Activity)`): gọi `AppUpdateManagerFactory.Create(activity)` ->
  `GetAppUpdateInfo()` (Java Task, await qua `.AsAsync<AppUpdateInfo>()` của `Android.Gms.Extensions`) -> nếu `UpdateAvailability() ==
  UpdateAvailable` và `IsUpdateTypeAllowed(AppUpdateType.Immediate)`, hoặc đang `DeveloperTriggeredUpdateInProgress` (lần trước người dùng thoát
  giữa chừng) -> `StartUpdateFlowForResult(..., AppUpdateOptions.NewBuilder(AppUpdateType.Immediate).Build(), requestCode)`. Toàn bộ UI chặn
  toàn màn hình, tải, cài, khởi động lại app đều do chính Play lo -- không có cấu hình/server nào của app cả, không dùng `UpdatePriority` (owner
  yêu cầu áp dụng cho mọi bản như nhau).
- Gọi ở `MainActivity.OnResume()` (đúng khuyến nghị chính thức của Google: bắt được cả trường hợp lần trước người dùng thoát giữa chừng lúc đang
  tải). Hệ quả đã ghi rõ trong comment: vì camera trong app / photo picker / chọn PDF cũng dùng `StartActivityForResult`, MỌI lần quay lại
  `MainActivity` (kể cả từ các picker đó, không chỉ chuyển app) đều kiểm tra lại -- nếu có bản mới sẽ chặn lại bằng màn Immediate, kể cả đang
  giữa chừng nhập ảnh. Đây là chủ ý theo đúng yêu cầu "bắt buộc", không phải lỗi.
- Gói thêm: `Xamarin.Google.Android.Play.App.Update` 2.0.1 (Apache-2.0, chính chủ Google -- không phải tự viết lại updater như bản đã gỡ đợt
  2026-09-29), tự kéo theo `Xamarin.GooglePlayServices.Tasks` 118.4.0 (đã có sẵn do Plugin.AdMob/Plugin.InAppBilling dùng chung). Không cần thêm
  quyền Android nào (INTERNET/ACCESS_NETWORK_STATE đã có sẵn cho AdMob/Billing). Đã cập nhật mục "BẢN QUYỀN" trong AboutPage.xaml.
- **Cách xác định đúng API**: NuGet C# binding cho `com.google.android.play:app-update` không có doc mẫu C# rõ ràng (tìm trên web chỉ ra đúng
  tên gói, không ra chữ ký hàm). Thay vì đoán, đã tải gói về rồi đọc thẳng metadata của chính file .dll (viết một tool nhỏ dùng
  `System.Reflection.Metadata`/`PEReader`, không cần load assembly thật nên không vướng thiếu `Mono.Android`) để liệt kê chính xác namespace,
  tên lớp, chữ ký hàm, giá trị hằng số (`AppUpdateType.Immediate = 1`, `UpdateAvailability.UpdateAvailable = 2`...) trước khi viết code -- tránh
  build-lỗi-sửa-lặp-lại nhiều vòng với một API bên ngoài không quen.
- Build `DocScanner.csproj -f net10.0-android`: BUILD SUCCEEDED, 0 lỗi.
- **Owner cần kiểm tra tay** (không mô phỏng được đầy đủ bằng build/unit test): toàn bộ luồng này chỉ thật sự kiểm chứng được khi có **2 bản đã
  đăng lên Play** (bản cũ cài trên máy, bản mới đã lên track nào đó) -- Play Console có hướng dẫn test bằng "Internal app sharing" hoặc track
  Internal testing. Chưa thử trên máy thật/máy ảo trong phiên này.

### Điều tra "chữ gợn sóng" trên máy thật + quét sách 2 trang (đợt 2026-10-04 tiếp, máy Note 10+ kết nối trực tiếp)
- Owner cắm máy thật nhưng `adb devices` không thấy -- Windows nhận USB nhưng toàn bộ interface Samsung (`SAMSUNG Android ADB Interface`...)
  ở trạng thái driver "Unknown". Sửa KHÔNG cần tải driver ngoài: `Disable-PnpDevice` rồi `Enable-PnpDevice` (PowerShell) cho các thiết bị đó ép
  Windows tìm lại driver đã có sẵn trong máy -- xong là `adb devices` thấy ngay (`RF8M81MKLTA`, `SM_N975F`). Ghi nhớ: đừng vội tải driver bên
  thứ ba (các link tìm được đều là mirror, không phải trang chính chủ Samsung) khi chưa thử cách này trước.
- Máy cài bản Release cũ (versionCode 3 / 1.2, từ đợt 2026-09-29) -- **không debug được** (`run-as` báo "package not debuggable"), nên không
  chép trực tiếp `files/documents/...` ra được; nút "Xuất PDF" cũng bị chặn ("Đã hết lượt xuất PDF miễn phí", bản này còn giới hạn 5 lần, từ
  TRƯỚC cả lúc owner tự bỏ paywall). Đã dùng cách khác để "nhìn" màn hình: `adb shell screencap` lấy ảnh, `adb shell uiautomator dump` lấy
  đúng toạ độ nút bấm (toạ độ đoán bằng mắt từ ảnh chụp sai một lần, bấm nhầm mở camera trong app -- thoát ngay bằng Back, không chụp ảnh nào;
  từ đó luôn dùng toạ độ từ `uiautomator dump`, không đoán nữa). Mẹo adb trong Git Bash: phải đặt `MSYS_NO_PATHCONV=1` TRONG CÙNG một lệnh
  Bash với lệnh `adb` (biến `export` ở lệnh Bash trước không giữ lại được sang lệnh sau, mỗi lần gọi tool Bash là một shell mới).
- Trang owner để sẵn (văn bản pháp luật, trang "17", có chú thích tay): phóng to kỹ thì phát hiện **không phải lỗi thuật toán** -- cái trông
  giống gợn sóng khi xem ảnh nhỏ thực ra là **nét bút tay** (gạch chéo, khoanh tròn, gạch dưới) của người review đè lên nhiều dòng liên tiếp;
  từng dòng chữ in vẫn thẳng tắp. Ghi lại để tránh "sửa" nhầm một thứ không phải lỗi.
- Owner chỉ ra đúng ảnh 6-9 (chụp thêm, sách giáo khoa đang mở) mới là ví dụ thật: ảnh 9 cố tình chụp 2 trang sách trong 1 ảnh. Nguyên nhân
  đúng như owner tự chẩn đoán: **sách mở có nếp gấp ở gáy** -- loại biến dạng khác hẳn "trang giấy phẳng bị cong/nghiêng" mà `PageBends` (chỉ bẻ
  cong 4 CẠNH) và `ContentAligner` (chỉ xoay đều TỪNG HÀNG quanh tâm trang) được thiết kế để sửa; cả hai không có khái niệm "một nếp gấp dọc
  ở giữa ảnh", nên không sửa được -- không phải lỗi code, mà là loại biến dạng nằm ngoài phạm vi 2 lớp sửa hiện có.

#### Quét sách 1 trang / 2 trang (tính năng mới, owner yêu cầu trực tiếp)
- Yêu cầu: mở camera trong app có chọn "1 trang" hay "2 trang"; chọn "2 trang" thì tự quét thành 2 ảnh, mỗi ảnh 1 trang.
- `Platforms/Android/Camera/BookSplit.cs` (mới): tách ảnh gốc thành 2 nửa chồng mép 8% mỗi bên (58% bề rộng/nửa) NGAY SAU khi chụp, lưu
  thành 2 file JPEG riêng (dựng thẳng theo EXIF trước khi cắt, nên 2 nửa không cần tag EXIF xoay nữa) -- rồi mỗi nửa đi qua ĐÚNG pipeline nhập
  ảnh một-trang có sẵn (tự dò mép, cắt phối cảnh, đen trắng...) không đổi gì cả, vì giờ mỗi file chỉ còn 1 trang sách + viền dư quanh gáy.
  Quyết định có chủ đích: cắt THẲNG theo cột ảnh (không dò gáy sách bằng thuật toán), đơn giản và đủ dùng vì bộ dò mép đã có sẵn tự lo phần còn
  lại cho từng nửa; không nắn cong riêng từng nửa ở bước này (owner chỉ yêu cầu "tách làm 2 ảnh", chưa yêu cầu nắn cong gáy sách -- nếu nửa
  trang vẫn còn hơi cong gần gáy sau khi tách, đó là việc có thể làm tiếp đợt sau, không phải lỗi của bản này).
- `DocumentCameraActivity.cs`: thêm 2 nút "1 trang" / "2 trang (sách mở)" ngay dưới thanh trên cùng (kiểu chip giống nút "Tự chụp"). Logic
  dò mép sống (`DocumentEdgeDetector.Live`, `CaptureStabilizer`) giữ nguyên hoàn toàn -- không cần biết đang ở chế độ nào, vì nó chỉ tìm "một
  tứ giác" và sách mở cũng là một tứ giác (chỉ khác tỉ lệ cạnh). Khi chụp xong ở chế độ "2 trang": `OnSaved` gọi `BookSplit.SplitInHalf` trên
  luồng nền, xong thêm CẢ HAI file vào `_photos` qua `AddPhoto` (hàm tách ra từ code thumbnail cũ, dùng chung cho cả hai chế độ); lỗi tách ảnh
  (hiếm) thì giữ nguyên ảnh gốc làm 1 trang, không mất ảnh đã chụp. `AndroidDocumentCamera`/`ImportCoordinator` phía sau không cần đổi gì --
  vốn đã nhận `_photos` là danh sách bao nhiêu ảnh cũng được.
- Build `DocScanner.csproj -f net10.0-android`: BUILD SUCCEEDED, 0 lỗi.
- Owner đồng ý gỡ bản cũ (mất tài liệu test cũ, gồm ảnh sách 6-9) để cài bản mới: `adb uninstall btk.docscanner` rồi
  `dotnet build DocScanner.csproj -f net10.0-android -t:Run` -- cài + chạy thành công trên Note 10+ (versionCode=4, versionName=1.3, danh sách
  tài liệu trống như dự kiến sau khi gỡ).
- Xuất AAB: `dotnet publish DocScanner.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=aab` -- BUILD SUCCEEDED, ký đúng upload key
  thật (`jarsigner -verify` -> "jar verified", các cảnh báo self-signed/no-timestamp giống hệt đợt 2026-09-29, không phải lỗi). File:
  `DocScanner/release/DocScanner-1.3-versionCode4.aab` (51,3 MB) -- owner tải lên Play Console. Số phiên bản 1.3/4 đã có sẵn trong
  csproj từ trước đợt này (chưa từng đăng lên Play ở version này), không cần tăng thêm.

### Owner test trực tiếp: khung xem trước còn tách "1 trang làm 2 mảnh hẹp" (đợt 2026-10-04 tiếp)
- Owner thử trên máy bằng bản vừa cài: xác nhận kết quả tách trang ĐÚNG (trang 46/47 tách sạch, đã phóng to kiểm chứng), nhưng khung xem
  trước khi bật "2 trang" **đôi khi chỉ khoanh được 1 trang** (bộ dò `DocumentEdgeDetector.Live()` không đổi gì, có lúc khoá vào đúng 1 trang
  thay vì cả khổ sách) -- lúc đó code vẫn chia đôi khung 1-trang đó ra 2 mảnh hẹp vô nghĩa (chính là 2 "trang" lạ, hẹp dọc trong tài liệu test
  owner vô tình tạo ra qua auto-chụp bắn liên tục lúc test).
- Sửa: `DocumentCameraActivity.LooksLikeSpread(Quad)` -- so tỉ lệ rộng:cao của khung dò được (trung bình cạnh trên/dưới chia trung bình cạnh
  trái/phải) với `SpreadMinAspect = 1.1` (1 trang đơn ~0,71:1, cả khổ sách ~1,41:1 -- 1,1 nằm an toàn dưới spread thật nhưng trên bất kỳ trang
  đơn nào dù chụp hơi nghiêng). Ở chế độ "2 trang", khung không đạt tỉ lệ này bị coi như "chưa sẵn sàng" (giống chưa dò được gì): không tính
  vào bộ ổn định (`CaptureStabilizer`) nên không bao giờ tự chụp, khung vẫn hiện (màu xanh dương, chưa xanh lá) kèm gợi ý "Lùi máy ra để thấy
  cả 2 trang sách". Chụp tay (bấm nút chụp) vẫn không bị chặn -- quyết định có chủ đích: tôn trọng lựa chọn rõ ràng của người dùng, chỉ chặn
  đường tự động dễ gây lỗi âm thầm.
- Kiểm chứng trực tiếp trên Note 10+: chĩa vào đúng 1 trang ở cả 2 bản (trước/sau sửa) -- bản cũ tự chụp + chia đôi thành 2 mảnh hẹp; bản mới
  giữ khung xanh dương, không tự chụp, đợi tới khi lùi ra đủ xa thấy cả 2 trang mới chuyển xanh lá và tự chụp đúng cặp trang.
- Dọn dẹp: 3 tài liệu test (18:07/18:28/18:33, tổng 21 trang rác do auto-chụp bắn liên tục trong lúc debug qua `adb input tap` mù trên màn hình
  đang động -- `uiautomator dump` không đọc được UI đang animate nên phải đoán toạ độ, có 2 lần đoán sai: lỡ mở camera hệ thống và trình chọn
  file, không hại gì, thoát bằng Back) -- đã xoá qua Home > ⋮ > Xoá, CHỈ giữ lại đúng tài liệu gốc của owner (17:20, 11 trang).

### Màn Cài đặt vẫn mô tả mô hình trả phí cũ "giới hạn 5 lần" (đợt 2026-10-04 tiếp, owner tự phát hiện)
- Owner: phần mua Pro trong Cài đặt vẫn nói "còn 5/5 lượt xuất PDF miễn phí" / Pro có "xuất không giới hạn (miễn phí chỉ 5 lần)" -- SAI so với
  model thật hiện tại (owner tự sửa trước đợt làm việc hôm nay: bỏ chặn, miễn phí xuất không giới hạn, chỉ hiện quảng cáo xen kẽ sau mỗi 5 lần).
- Xác nhận trong code: `ExportCoordinator.ExportAsync` dòng chặn `if (!license.State.CanExport && !await OfferUpgradeAsync())` đã bị owner COMMENT
  OUT, nhưng `license.RecordExport()` (dòng đếm `ExportsUsed`) vẫn chạy mỗi lần xuất -- nghĩa là bộ đếm cũ (`LicenseState`/`TrialPolicy`,
  `ExportsRemaining`/`FreeExportLimit=5`) vẫn cộng dồn vô nghĩa phía sau, không còn ai dùng để chặn, nhưng `SummaryText` (hiển thị ở Cài đặt) vẫn
  tính theo bộ đếm đó nên hiện sai (và sau 5 lần xuất thật sẽ hiện "Đã dùng hết lượt" dù app không hề chặn gì).
- Sửa phạm vi hẹp (đúng yêu cầu owner -- chỉ phần hiển thị, KHÔNG động vào logic chặn đã bị owner tự tắt, tôn trọng quyết định của owner):
  `LicenseState.SummaryText` (DocScanner.Core/Licensing/LicenseState.cs) bỏ hẳn nhánh đếm "còn X/5", giờ chỉ còn 2 trạng thái: "Đã nâng cấp Pro"
  / "Đang dùng bản miễn phí (có quảng cáo)", không phụ thuộc `ExportsUsed` nữa. `SettingsPage.xaml` mục "Bản Pro có gì": bỏ dòng
  "Xuất PDF không giới hạn (miễn phí chỉ 5 lần)" (sai -- miễn phí ĐÃ không giới hạn), gộp thành "Không còn quảng cáo (bản miễn phí có banner ở
  mọi màn hình và quảng cáo xen kẽ sau mỗi 5 lần xuất PDF)" -- đúng 2 cơ chế quảng cáo đã làm đợt trước trong cùng ngày.
- Test: `LicenseTests.cs` -- sửa `Summary_text_reflects_pro_trial_and_exhausted_states` (test cũ dựa đúng vào format "còn X/Y" giờ sai) thành
  `Summary_text_only_ever_distinguishes_pro_from_free`, xác nhận SummaryText không đổi dù `ExportsUsed` là 0, 1, hay vượt xa giới hạn cũ.
  `DocScanner.Core.Tests`: 189/189 PASS.

### Build cuối đợt + cài máy thật (đợt 2026-10-04, owner yêu cầu "build bản thật lên cho tôi và tạo file .aab")
- Build lại AAB (ghi đè `DocScanner/release/DocScanner-1.3-versionCode4.aab`, 50,8 MB, `jarsigner -verify` -> "jar verified") và APK
  Release riêng để cài máy (`dotnet publish ... -p:AndroidPackageFormat=apk`, ký cùng upload key -- khác khoá debug nên phải `adb uninstall`
  bản Debug đang cài rồi `adb install` bản Release mới, không thể cài đè trực tiếp).
- Đã cài thành công lên Note 10+ (`adb install` -> Success), xác nhận đúng versionCode=4/versionName=1.3 qua `dumpsys package`. Máy tự khoá
  màn hình (lock screen có mật khẩu, không phải màn hình chờ AOD) ngay sau đó -- KHÔNG có mã mở khoá nên dừng lại, không chụp màn hình Cài đặt
  xác nhận trực quan được; độ tin cậy của bản sửa dựa trên 189 unit test PASS (gồm test `SummaryText` mới) + build/install thành công, chưa có
  xác nhận bằng mắt trên máy. **Owner cần tự mở khoá máy và vào Cài đặt xem lại nếu muốn xác nhận trực quan.**

### Sửa 3 nguyên nhân gây lag (đợt 2026-10-05, owner báo "nhập ảnh phải đợi ~1s", "chuyển màn hình hơi chậm")
Review code (không sửa) trước, tìm 3 nguyên nhân cụ thể; owner chọn hướng cho từng cái rồi mới sửa. **Theo yêu cầu owner, đợt này CHƯA build /
chạy thử trên máy -- chỉ sửa code, chờ owner tự build.**

1. **Banner quảng cáo bị tạo lại (và load lại ad) ở MỌI lần chuyển màn hình** -- nguyên nhân chính của "chuyển màn hình chậm", vì nó xảy ra ở
   MỌI điều hướng, không riêng lúc nhập ảnh. Gốc: mọi Page/ViewModel đăng ký `AddTransient` (`MauiProgram.cs`), nên Shell tạo Page mới mỗi lần
   `GoToAsync`; `AdBannerView` (chứa `Plugin.AdMob.BannerAd`) từng nhúng trong 10/10 trang -> 10/10 trang này tự tạo native `AdView` mới + gọi
   `LoadAd()` mới mỗi lần ghé qua.
   - Owner chọn hướng "banner cố định ngoài Shell, ẩn hẳn khi mua Pro". Đã làm: xoá `Views/AdBannerView.xaml(.cs)` và bỏ nó khỏi cả 10 trang XAML
     (SettingsPage, AboutPage, SignaturePage, DocumentPage, HomePage, ViewerPage, PdfViewerPage, ExportsPage, ResultPage, CropPage -- mỗi trang
     cũng rút gọn lại `RowDefinitions`/`Grid.RowSpan` của `ExportOverlay` theo đúng số dòng còn lại). Thay bằng **một `AdView` (Google Mobile Ads
     SDK gốc, namespace `Android.Gms.Ads`, KHÔNG qua wrapper `Plugin.AdMob.BannerAd`) duy nhất**, tạo một lần trong `Platforms/Android/MainActivity.
     OnCreate`: bọc view gốc MAUI (lấy từ `FindViewById(Android.Resource.Id.Content)` sau `base.OnCreate`) vào một `LinearLayout` dọc, AdView nằm
     dưới cùng. Vì MAUI chỉ thay nội dung của chính view gốc đó khi Shell/`Window.Page` đổi (kể cả từ SplashPage sang AppShell -- xem `App.xaml.cs`
     `Window.Page = main`), `AdView` này không bao giờ bị tạo lại hay load lại khi chuyển màn hình nữa.
   - `AdView.Visibility` theo `IAdsService.ShowAds` (ẩn hẳn + KHÔNG gọi `LoadAd` từ đầu khi đã là Pro lúc khởi động -- không chỉ ẩn UI mà còn đỡ
     luôn request mạng), cập nhật qua `IAdsService.Changed` giống cách `AdBannerView` cũ làm. `Resume()`/`Pause()`/`Destroy()` gọi đúng theo
     lifecycle của Activity (`OnResume`/`OnPause`/`OnDestroy`) -- yêu cầu bắt buộc của AdMob SDK cho `AdView` tự quản lý, trước đây `Plugin.AdMob`
     lo việc này, giờ ta tự lo.
   - API chính xác của `Android.Gms.Ads.AdView`/`AdSize`/`AdRequest` (namespace này pulled transitively qua gói `Xamarin.GooglePlayServices.
     Ads.Lite`, do `Plugin.AdMob` kéo theo -- KHÔNG cần thêm `PackageReference` nào) được xác minh bằng cách đọc thẳng PE metadata của file .dll
     trong `~/.nuget/packages` (tool `System.Reflection.Metadata`/`PEReader` viết tạm trong scratchpad, không load assembly thật nên không vướng
     thiếu `Mono.Android` -- cùng kỹ thuật đã dùng cho gói Play In-App Update đợt 2026-10-04), KHÔNG đoán theo tài liệu/ví dụ trên mạng: xác nhận
     đúng `BaseAdView` (lớp cha của `AdView`) có `AdUnitId`/`AdSize` (set), `LoadAd(AdRequest)`, `Resume()`/`Pause()`/`Destroy()`; `AdRequest.
     Builder().Build()`.
   - ID quảng cáo test khi `Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds == true` (Debug, hoặc Release chưa có ID thật): dùng ID banner
     test CHÍNH THỨC của Google (`ca-app-pub-3940256099942544/6300978111`, công khai tại developers.google.com/admob/android/test-ads) vì
     `Plugin.AdMob` không lộ hằng số này ra public API -- trước đây `BannerAd` của `Plugin.AdMob` tự thay bên trong, giờ AdView gốc nên phải tự
     làm; Release có ID thật (`AdsConfig.HasRealIds`) vẫn dùng `AdsConfig.BannerAdUnitId` như cũ.

2. **2-3 lần ghi `doc.json` đồng bộ trên UI thread, chạy TRƯỚC KHI màn hình mới kịp hiện ra** -- `store.Create()` (+ `store.MoveToFolder` nếu
   đang trong thư mục) và `BackgroundImporter.Start` -> `ImportService.AddPlaceholders` -> `store.Update()` đều là `File.WriteAllText` +
   `File.Move` đồng bộ, gọi thẳng trên tiếp diễn UI thread (không `Task.Run`), TRƯỚC `Shell.Current.GoToAsync` -- tức trước khi người dùng thấy
   bất cứ gì, kể cả khung "Đang tải...". Sửa: `ImportCoordinator.cs` thêm `CreateAndStartAsync` (bọc `document()` + `importer.Start(...)` trong
   một `Task.Run` duy nhất), dùng ở cả 3 luồng nhập (thư viện, PDF, camera) thay cho gọi trực tiếp. `DocumentStore` đã tự khoá (`lock _gate`) nên
   chạy off UI thread an toàn, không đổi thứ tự/đúng-sai gì khác.

3. **`DocumentViewModel.Sync()` refresh TOÀN BỘ ô ảnh mỗi khi có 1 sự kiện nhập ảnh, không chỉ ô thay đổi** -- `BackgroundImporter.Changed` nổ
   ra ~1 lần/ảnh trong batch; `OnImportChanged` gọi `Sync()`, và khi danh sách trang còn là tiền tố (luôn đúng lúc đang nhập thêm), `Sync()` cũ
   chạy `foreach (PageItem item in Pages) item.Refresh();` cho MỌI trang đã có trước khi thêm trang mới -- O(n) `File.Exists()` mỗi lần batch
   nhích thêm 1 ảnh, với tài liệu nhiều trang + batch nhiều ảnh thì thành hàng chục nghìn lần gọi không cần thiết. Các trang đã `Ready`/`Failed`
   chỉ đổi qua `OnPageUpdated`/`OnImportPageChanged`, hai đường này ĐÃ nhắm đúng đúng 1 trang rồi, nên refresh lại ở `Sync()` là dư. Sửa: chỉ
   refresh trang còn `Importing`/`Pending`/`Preview` (còn trong pipeline), bỏ qua trang đã ổn định -- comment trong code giải thích rõ lý do.

**Chưa làm** (đợt này owner yêu cầu dừng ở mức sửa code): chưa `dotnet build`/`dotnet publish`/cài thử trên máy. Rủi ro còn treo: `AdView` gốc
mới viết tay (không qua `Plugin.AdMob`) chưa được build để bắt lỗi biên dịch/thiếu using, và hành vi thật trên máy (banner có hiện đúng, có load
ảnh quảng cáo, có ẩn đúng khi Pro) chưa được xác nhận -- owner cần tự build (`dotnet build DocScanner.csproj -f net10.0-android`) trước khi chạy.

### Xem PDF kiểu cuộn liên tục + zoom, thay cho từng trang + nút ‹ › (đợt 2026-10-05 tiếp)
Owner: màn xem PDF (`PdfViewerPage`, mở từ "PDF đã xuất") đang xem từng trang một kiểu next/previous, muốn "xem như một trình xem PDF hoàn hảo,
có thể kéo lên kéo xuống" + "có cả zoom nữa" + "đảm bảo mở cái là xem ngay, có thể xem lazy load". Đã build qua (`dotnet build`, BUILD SUCCEEDED).

- `Platforms/Android/PdfScrollView.cs` (mới): 1 `Android.Views.View` thuần (không qua MAUI `GraphicsView`, vì mỗi khung vẽ ở đây ghép nhiều
  Bitmap đã giải mã sẵn -- cách phần còn lại của app luôn làm việc này là thẳng lên Canvas/ImageView native, xem `ZoomController`/`ResultPage`;
  qua `ICanvas` của MAUI sẽ phải bọc mỗi Bitmap thành `IImage` mỗi khung, không lợi gì). Xếp mọi trang theo chiều dọc trong một không gian toạ độ
  ảo ("content space", đơn vị px ở zoom = 1 = vừa khít chiều rộng khung nhìn, mỗi trang giữ đúng tỉ lệ khung hình riêng của nó, cách nhau 24px);
  "camera" có `_scale` (zoom) + `_panX/_panY` (toạ độ góc trên-trái khung nhìn trong content space). Vuốt = `GestureDetector.OnScroll` dời
  pan; thả tay = `OnFling` nạp cho `OverScroller` (vật lý trôi thật, không tự viết công thức); pinch = `ScaleGestureDetector` zoom quanh đúng
  điểm 2 ngón; double-tap = zoom nhanh 2.5x / về lại vừa khung. Pan ngang chỉ có khi đã zoom quá 1x (mỗi trang vốn đã vừa khít chiều rộng).
- Bộ nhớ: mỗi trang chỉ giải mã 1 lần (ở `PageEdge = 2900`, giống độ nét bản xem từng-trang cũ), sau đó CHỈ co giãn qua ma trận Canvas khi
  zoom -- không giải mã lại. Chỉ giữ bitmap của các trang đang chạm khung nhìn (±1 trang mỗi phía, `KeepAroundVisible`); trang ra khỏi vùng đó
  bị `Recycle()` ngay trong `OnDraw`, nên cuộn qua tài liệu dài không giữ quá vài bitmap ~24 MB cùng lúc.
- "Mở cái là xem ngay" + lazy load: `PdfPages.Size(index)` (mới, cùng file `PdfPages.cs`) đo kích thước 1 trang (mở/đóng `PdfRenderer.Page`,
  KHÔNG render) -- rẻ hơn hẳn giải mã. `SetPages` đo kích thước từng trang MỘT, tuần tự, trong 1 `Task.Run`, và `Post` kết quả về UI thread
  ngay sau mỗi trang (không đợi đo hết cả tài liệu rồi mới dựng layout) -- trang 1 có khung ngay trong vài ms, bắt đầu giải mã bitmap thật của
  nó ngay, các trang sau nối dần vào layout khi đo xong (mỗi lần chỉ append O(1), không dựng lại từ đầu). Khung đang chờ bitmap hiện ô xám nhạt
  giữ đúng tỉ lệ (không giật layout khi bitmap về).
- `Views/PdfViewerPage.xaml(.cs)`: bỏ hẳn `Image x:Name="Picture"` + thanh ‹ Trang X/Y › cũ, thay bằng `<ContentView x:Name="Surface" />` trống
  -- code-behind lấy `Surface.Handler.PlatformView` (một `ViewGroup`) rồi `AddView` thẳng `PdfScrollView` vào đó, đúng kiểu "mượn view native của
  MAUI rồi tự quản" mà `ZoomImageHost` đã làm cho màn xem ảnh từng trang. Một viên pill nhỏ "Trang X/Y" nổi ở dưới (giống `ViewerPage`) cập nhật
  theo trang đang ở đầu khung nhìn (`PdfScrollView.PageChanged`), không còn nút ‹ ›. `PdfViewerViewModel` bỏ hết `Go`/`CanGoPrevious`/
  `CanGoNext`/`SetPageCount` (điều hướng rời rạc không còn cần), chỉ còn `ReportVisiblePage(index, count)` cho viên pill.
- Lỗi build gặp và đã sửa: XAML comment chứa `--` bị XML cấm (phải đổi `--`/`;`); `View`/`Paint`/`RectF`/`Color` ambiguous giữa `Android.*` và
  `Microsoft.Maui.*` (project có global using cả hai) -- sửa bằng `using X = Android.Y.X;`, đúng kiểu file `ZoomController.cs` đã làm sẵn cho
  `View`; `PostOnAnimation` chỉ nhận `Java.Lang.IRunnable`, không có overload `Action` như `Post` -- cho `PdfScrollView` tự implement
  `IRunnable.Run()` thay vì bọc lambda mỗi khung.
- **Owner cần tự thử trên máy** (chỉ mới build qua, chưa cài/chạy): cuộn mượt qua nhiều trang, pinch zoom + double-tap, pan khi đã zoom, mở
  một PDF nhiều trang xem trang đầu có hiện ngay không.

### Áp theme Material 3 Expressive của owner vào màn Trang chủ (đợt 2026-10-05 tiếp)
Owner gửi 1 bản thiết kế tự làm (Artifact "Design", https://claude.ai/artifact/Dwztu6kjeiwL2rP2HXjucE, artboard "Material 3 Expressive" 390x844)
và yêu cầu áp style đó vào app. Bản thiết kế chỉ có 1 màn hình mẫu (Trang chủ); đã làm đúng màn đó theo đúng thiết kế + đưa 2 token màu nền
(`Primary`, `Secondary`) lên `Resources/Styles/Colors.xaml` để lan sang toàn app (mọi trang đang dùng `{StaticResource Primary}` đổi màu theo,
không cần sửa từng trang) -- CÁC MÀN KHÁC (Document, Crop, Result, Settings...) CHƯA được dựng lại theo thiết kế này, chỉ đổi màu nhấn; nền/thẻ
của chúng vẫn dùng bảng xám-xanh cũ. Đã build qua (BUILD SUCCEEDED), CHƯA cài thử trên máy.

- `Resources/Styles/Colors.xaml`: `Primary` `#1A5FD6` -> `#5B4FE0` (tím), `Secondary` `#E3ECFC` -> `#E8E2FB`, `PrimaryDark` (màu nhấn ở Dark
  mode) `#8AB4FF` -> `#B7ACFF`. `Views/ToolButton.cs` (thanh công cụ dưới của Document/Crop/Result/Signature/Viewer/PdfViewer) đang tô màu
  cứng trong code (`#1A5FD6`/`#37404D`, không qua `StaticResource`) -- sửa luôn 2 hằng số đó sang tím/xám-tím mới để đồng bộ, vì nếu không sửa
  thì các thanh công cụ này vẫn xanh cũ dù mọi nơi khác đã đổi.
- `Views/HomePage.xaml` dựng lại hoàn toàn theo đúng bố cục thiết kế: header riêng của trang (không dùng `ContentPage.ToolbarItems`/thanh Shell
  nữa, vì thiết kế có nút tròn màu mà toolbar native của Shell không vẽ được) -- `Shell.NavBarIsVisible="False"`, tự vẽ tên tài liệu/thư mục +
  4 nút tròn (Tìm kiếm, Nhập PDF, Thư mục mới -- tô đặc màu tím như thiết kế, Cài đặt). Vì bỏ thanh Shell nên mất luôn nút back khi đang ở trong
  thư mục -- đã tự thêm nút back riêng (`HomeViewModel.GoBackCommand`, chỉ hiện khi `InFolder`), nút back cứng của Android/gesture back không bị
  ảnh hưởng (`HomePage.xaml.cs.OnBackButtonPressed` không đụng tới cơ chế điều hướng, vẫn `Shell.Current.GoToAsync`).
- Thẻ tài liệu/thư mục: bo góc 18px, nền trắng, đổ bóng nhẹ, ô icon 50x50 bo 14px (tài liệu: ảnh thumbnail thật trên nền tím nhạt -- thiết kế
  dùng icon chung vì là mockup không có dữ liệu thật, giữ lại thumbnail thật vì hữu ích hơn và đây là app thật có dữ liệu; thư mục: icon màu hổ
  phách, khác màu tài liệu để phân biệt 2 loại có ý nghĩa thật, không phải tô màu tuỳ ý theo "loại tài liệu" như 5 màu trong bản mockup -- bản
  mockup không có khái niệm "loại tài liệu" nào trong dữ liệu thật của app, tô 5 màu ngẫu nhiên sẽ là bịa đặt không có cơ sở). Thêm viên tròn số
  trang cạnh mỗi thẻ tài liệu (`ViewModels/Items.cs`: `DocumentItem.PageCountText`, tách riêng khỏi `Subtitle` vốn đã gộp số trang vào câu).
  Nút quét (FAB) tròn nổi phía trên thanh dưới vốn đã có sẵn kiểu này từ trước, chỉ thêm bóng màu tím mờ (`Border.Shadow`) cho giống thiết kế.
  Nút lọc "Tất cả / Thư mục" trong thiết kế KHÔNG đưa vào: đây là bộ lọc hiển thị không có logic tương ứng trong `HomeViewModel` hiện tại
  (danh sách luôn gộp thư mục + tài liệu), thêm vào sẽ là thêm tính năng mới ngoài yêu cầu "áp style", không phải chỉnh giao diện.
- Có cân nhắc đổi phông chữ sang 'Plus Jakarta Sans' (phông trong thiết kế) nhưng KHÔNG làm: kho font Google Fonts (`google/fonts` trên
  GitHub, đã kiểm tra trực tiếp) chỉ có file variable font cho họ chữ này, không có từng file tĩnh theo độ đậm (500/600/700/800) như app đang
  cần; variable font trên Android qua .NET MAUI không chắc chọn đúng độ đậm ở API 26 (bản thấp nhất app hỗ trợ). Giữ nguyên OpenSansRegular/
  OpenSansSemibold đang dùng -- hai phông sans-serif này đủ gần nhau về cảm giác, rủi ro thấp hơn nhiều so với tích hợp variable font chưa rõ
  hành vi. Owner muốn đúng phông thật thì cần tự tải các file .ttf tĩnh (ví dụ từ fonts.google.com, chọn đúng 4 độ đậm) bỏ vào
  `Resources/Fonts/` rồi báo lại.
- **Hỏi owner**: có muốn dựng lại các màn còn lại (Document, Crop, Result, Settings, Signature, Exports, About...) theo đúng ngôn ngữ thiết kế
  này luôn không (bo góc card, nền lavender, icon tile màu...), hay chỉ cần đổi màu nhấn như hiện tại là đủ? Đây là việc lớn (sửa layout nhiều
  file), nên dừng lại hỏi trước khi làm tiếp thay vì tự quyết và có thể làm sai ý.

### Áp theme tím cho TOÀN BỘ các màn còn lại + xác nhận trên máy thật (đợt 2026-10-05 tiếp, owner: "có chứ. dựng các trang còn lại theo theme này")
- Thêm 5 token màu mới (sáng + tối) vào `Resources/Styles/Colors.xaml`: `Surface`/`SurfaceDark` (nền trang), `CardBackground`/
  `CardBackgroundDark` (thẻ/thanh công cụ), `OnSurface`/`OnSurfaceDark` (chữ chính), `Muted`/`MutedDark` (chữ phụ/hint), `Divider`/`DividerDark`
  (đường kẻ mảnh) -- thay cho hàng chục màu xám-xanh viết cứng (hex) rải rác trong từng file XAML trước đó. `Styles.xaml` (Page/Shell mặc định)
  cũng đổi sang 2 token này.
- Sửa toàn bộ 10 trang còn lại (DocumentPage, CropPage, ResultPage, SettingsPage, SignaturePage, ExportsPage, AboutPage, ViewerPage,
  PdfViewerPage) sang dùng các token trên thay cho hex cứng, **giữ đúng cặp Light/Dark riêng cho từng trang** (bài học từ chính lỗi của bản thân
  ở `HomePage.xaml` đợt trước: dựng lại hoàn toàn theo mockup nhưng quên mất chế độ tối, chỉ có 1 bộ màu sáng cứng -- đã quay lại sửa luôn
  `HomePage.xaml` thêm đủ cặp Dark trong đợt này). Những màu KHÔNG đổi (có chủ đích): `ViewerPage`/`PdfViewerPage` nền `#101010` (màn xem ảnh
  luôn tối kiểu phòng tối, không theo theme sáng/tối của app); màu mực chữ ký (đen/xanh dương/đỏ) trong `SignaturePage` (màu mực thật, không
  phải màu giao diện); `#E53935` (icon PDF, màu thương hiệu PDF phổ biến); `#D32F2F` (hành động xoá, màu cảnh báo).
- Quét thêm và sửa các chỗ màu cũ `#1A5FD6` viết cứng trong code C# (không qua resource, nên không tự đổi theo bước trên): `ToolButton.cs`
  (đã sửa đợt trước), `StampEditor.cs` (viền + tay cầm của chữ ký đang chọn trên trang), `CameraOverlays.cs` (viền/nền khung dò giấy lúc "chưa
  sẵn sàng" trong camera trong app -- tiện sửa luôn một chỗ không nhất quán có sẵn từ trước: viền và nền khi "chưa sẵn sàng" dùng 2 màu xanh
  khác nhau, 66,133,244 và 26,95,214, giờ cả hai cùng là tím mới), `DocumentCameraActivity.cs` (4 chỗ: nền số đếm trang, nút "Xong", chip chế
  độ 1/2 trang, công tắc tự chụp). KHÔNG đổi màu xanh lá / cam / xanh dương trong `QuadEditor.cs` (viền khung khi chỉnh tay) vì đó là mã màu
  trạng thái có ý nghĩa riêng (xanh lá = tự dò, cam = toàn khung, xanh dương = đã chỉnh tay), không phải màu thương hiệu.
- App icon + splash: `Resources/AppIcon/appicon.svg` (gradient nền, tự vẽ SVG riêng, không chỉ phụ thuộc `Color=` trong csproj),
  `Resources/Splash/splash.svg` (2 màu tô đường kẻ/nếp gấp trang trong icon), `DocScanner.csproj` (`MauiIcon`/`MauiSplashScreen` Color=),
  `Platforms/Android/Resources/values/colors.xml` (colorPrimary/colorPrimaryDark/colorAccent -- chi phối màu thanh trạng thái Android trước khi
  C# kịp chạy), `Views/SplashPage.xaml` (gradient + màu chữ) -- tất cả đổi từ xanh dương `#1A5FD6` sang tím `#5B4FE0` cho đồng bộ từ lúc mở app.
- Build qua (`dotnet build`, BUILD SUCCEEDED) sau 2 vòng sửa lỗi: ambiguous `View`/`Paint`/`RectF`/`Color` giữa `Android.*` và `Microsoft.Maui.*`
  (đã có từ đợt PdfScrollView trước, không liên quan đợt này).
- **Thử trên máy ảo thất bại**: máy ảo Pixel 7 API 36 (`pixel_7_-_api_36_0`) liên tục crash vài giây sau khi boot xong, kể cả bật
  `-gpu swiftshader_indirect` (render phần mềm) -- log chỉ ra lỗi liên quan driver đồ hoạ Windows ("A device attached to the system is not
  functioning" / "Failed to find ColorBuffer"), không phải lỗi của app. Không sửa được trong phiên này (vấn đề môi trường máy owner, không phải
  code) -- owner cân nhắc cập nhật driver GPU hoặc dùng máy thật để test.
- **Chuyển sang máy thật theo yêu cầu owner ("build lên máy thật cho tôi xem")**: lần build đầu lỗi `ADB0020 IncompatibleCpuAbiException` (APK
  build dở cho kiến trúc x86_64 của máy ảo trước đó bị đẩy nhầm sang máy thật arm64, do build/cài liên tiếp nhắm 2 thiết bị khác kiến trúc) --
  build lại lần 2 (chỉ còn máy thật kết nối) qua trót lọt. Cài + mở app xác nhận bằng ảnh chụp màn hình thật: Trang chủ (chế độ tối của máy)
  hiện đúng theme tím mới, tài liệu owner tự tạo (10 trang, ảnh chụp sách giáo khoa) hiện đúng trên màn Tài liệu (nền tối, thẻ bo góc, nút
  Xem/Sửa dạng pill, nút Xuất PDF tô tím). Owner có vẻ đang tự bấm thử trực tiếp trên máy trong lúc tôi thao tác qua adb -- đã dừng lại, không
  tự động hoá thêm thao tác chạm trên máy nữa để tránh chồng lấn với owner; đã dọn 3 ảnh test tự đẩy vào thư viện ảnh trước đó (không cần nữa).

### Xem PDF ra màn đen + quảng cáo "biến mất": cùng một nguyên nhân gốc, sửa bằng Handler thật (đợt 2026-10-05 tiếp)
Owner báo trên máy thật: "xem pdf đang lỗi" (màn đen, chỉ có viên "Trang 1/10" nổi) và sau đó "quảng cáo của tôi sao lại bị xoá". Cả hai đều do
cùng một kiểu lỗi: gắn 1 native View thẳng vào cây view của MAUI bằng cách "lấy native container rồi tự AddView" (không qua Handler của MAUI) --
MAUI không biết tới view đó nên không bao giờ đo/xếp kích thước cho nó đúng, hoặc chính MAUI tự vẽ đè lên mà không hay biết có gì ở dưới.

- **PdfScrollView** (màn xem PDF, dựng ở đợt trước cùng ngày): `PdfViewerPage.xaml.cs` từng lấy `ContentView.Handler.PlatformView` (một
  `ContentViewGroup` của MAUI) rồi `AddView(pdfScrollView, ...)` thẳng vào đó. Thêm log chẩn đoán (`Android.Util.Log`) rồi xem bằng
  `adb logcat` trên máy owner: xác nhận `OnSizeChanged` KHÔNG BAO GIỜ chạy, `Width`/`Height` của view luôn là 0 -- `ContentViewGroup` không đo
  cho "con lạ" mà nó không tự quản lý. Sửa đúng cách: `Views/PdfScrollSurface.cs` (`View` rỗng, chỉ để khai trong XAML) +
  `Platforms/Android/PdfScrollSurfaceHandler.cs` (`ViewHandler<PdfScrollSurface, PdfScrollView>`, `CreatePlatformView() => new(Context)`) +
  đăng ký `ConfigureMauiHandlers` trong `MauiProgram.cs` -- giờ MAUI tự đo/xếp layout cho nó như mọi view khác. `PdfViewerPage.xaml` đổi
  `<ContentView x:Name="Surface" />` thành `<views:PdfScrollSurface x:Name="Surface" />`; code-behind chỉ còn lấy `Surface.Handler.PlatformView
  as PdfScrollView` (không tự AddView nữa). Owner xác nhận lại trên máy: cuộn được, thấy "Trang 9/10" -- hết màn đen.
- **Banner quảng cáo** (đợt sửa hiệu năng, cùng ngày trước đó): `MainActivity.cs` từng lấy root view của toàn Activity, bọc vào 1
  `LinearLayout` tự tạo rồi nhét `AdView` xuống dưới. Hai lỗi chồng lên nhau, tìm bằng log + `AdListener` thêm tạm:
  1. `decorContent.ChildCount == 0` lúc `OnCreate` chạy (MAUI chưa kịp gắn nội dung vào Activity -- SplashPage còn đang dựng AppShell) khiến
     toàn bộ hàm bị bỏ qua lặng lẽ, không log, không lỗi -- banner không bao giờ được tạo. Sửa tạm: gọi lại ở `OnResume` (tự lặp lại tới khi
     thành công) -- nhưng vẫn chưa hết lỗi.
  2. Dù gắn được, `AdView` đo ra `Height=0` trong `LinearLayout` (đặt cứng theo px từ `AdSize.Banner.GetHeightInPixels()` thì hết 0, nhưng dù
     đúng kích thước + quảng cáo đã `OnAdLoaded` thành công, banner VẪN không hiện trên máy thật -- vì MAUI tự vẽ đè nội dung của nó lên toàn
     bộ vùng, không biết (và không tôn trọng) việc nó chỉ còn được cấp một phần màn hình sau khi bị bọc trong LinearLayout của tôi.
  - Bỏ hẳn cách "bọc root view của Activity", chuyển sang **1 `AdView` dùng chung cho cả app, gắn qua Handler thật** giống PdfScrollView:
    `Views/AdBannerSurface.cs` + `Platforms/Android/AdBannerSurfaceHandler.cs` (`CreatePlatformView()` trả về CÙNG MỘT instance `AdView` tĩnh
    (static) cho mọi trang -- nếu đã có, tự gỡ khỏi trang cũ (`RemoveView`) trước khi trang mới nhận; `DisconnectHandler` bỏ trống có chủ đích
    để không huỷ `AdView` dùng chung chỉ vì 1 trang bị rời đi). Thêm `<views:AdBannerSurface HeightRequest="50" />` vào đúng vị trí cũ trên cả
    10 trang (Home, Document, Crop, Result, Settings, Signature, Exports, About, Viewer, PdfViewer -- không có Splash), mỗi trang tự co giãn
    `RowDefinitions`/`Grid.RowSpan` của `ExportOverlay` theo đúng số dòng. Vừa đúng kỹ thuật (MAUI tự đo/vẽ, không đè) vừa giữ đúng mục tiêu ban
    đầu của đợt sửa hiệu năng (không tạo/tải lại quảng cáo mỗi lần chuyển trang). Owner xác nhận trên máy thật: banner hiện đúng, thấy chữ
    "Quảng cáo" + nội dung quảng cáo thật.
- **Owner báo tiếp**: "thi thoảng ... banner đen thui, như không có quảng cáo nào được load" -- đúng vậy: banner cũ luôn `Visibility=Visible`
  ngay khi tạo, kể cả trước khi `LoadAd` có kết quả, hoặc khi một lần làm mới quảng cáo định kỳ (AdMob tự refresh banner, quan sát log thấy
  ~mỗi 60-90 giây) bị lỗi/không có quảng cáo. Sửa: `AdBannerSurfaceHandler` thêm `AdListener` (`OnAdLoaded` -> hiện, `OnAdFailedToLoad` -> ẩn),
  mặc định ẩn (`_loaded = false`) cho tới khi có quảng cáo thật. Owner yêu cầu đúng vậy: "ẩn đi cho đẹp" thay vì để trống/đen. Kiểm tra log
  `adb logcat` thấy nhiều lần `mediation_fill_result` thành công lặp lại theo chu kỳ, xác nhận cơ chế load/refresh hoạt động đúng; 1 lần chụp
  màn hình rơi đúng lúc giữa 2 lần tải thấy banner ẩn (đúng hành vi mới, không phải lỗi).
- **Bài học chung cho cả 2 lỗi**: mọi lần cần nhúng 1 native View thuần (không phải control MAUI có sẵn) vào cây giao diện, PHẢI đi qua
  `ViewHandler<TVirtualView, TPlatformView>` + `ConfigureMauiHandlers` -- không được "lấy native container rồi tự AddView" dù trông có vẻ đơn
  giản hơn, vì MAUI sẽ không biết/không tôn trọng view đó trong các lần đo-vẽ của chính nó. `ZoomImageHost`'s "mượn view native" chỉ an toàn vì
  nó mượn CHÍNH view MAUI đã tạo và đang quản lý (một `Image`), không thêm view lạ vào cây.

### Thêm tìm kiếm cho màn "PDF đã xuất" (đợt 2026-10-05 tiếp, owner yêu cầu)
`ExportsViewModel` thêm `IsSearching`/`SearchText`/`ToggleSearchCommand` + `EmptyText` theo đúng khuôn mẫu đã có ở `HomeViewModel`
(`TextSearch.Matches`, không phân biệt hoa/thường/dấu). Giữ nguyên danh sách gốc (`_all`) trong bộ nhớ, lọc lại vào `Files` mỗi khi gõ tìm --
không đọc lại thư mục exports mỗi lần gõ. `ExportsPage.xaml` thêm `ToolbarItem` kính lúp (trang này vẫn dùng thanh Shell gốc, không như
HomePage) + `SearchBar` ẩn/hiện theo `IsSearching`. Build qua, chưa có xác nhận hình ảnh trên máy owner (màn hình owner lúc đó bị cửa sổ nổi
YouTube/Zalo che mất nút "PDF đã xuất").
