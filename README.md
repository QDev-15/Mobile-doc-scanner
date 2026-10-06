# Doc Scanner (Mobile-doc-scanner)

App quét tài liệu trên điện thoại: chụp ảnh / nhập ảnh có sẵn -> tự dò mép giấy (hoặc kéo tay) -> cắt phối cảnh ->
đen trắng/màu -> nhiều trang -> xuất PDF. Viết bằng **.NET MAUI**, hiện chỉ nhắm **Android** (`net10.0-android`, min
SDK 26). Package `btk.docscanner`, tên hiển thị "Doc Scanner" -- sản phẩm cá nhân của tác giả, không liên quan công ty.

Repo này mới được tách ra từ một monorepo lớn hơn (2026-10-06); chi tiết + lịch sử phát triển đầy đủ nằm ở
[CLAUDE.md](CLAUDE.md) (phần "Ghi chú tách repo" ở đầu file nên đọc trước).

## Cấu trúc

| Thư mục | Nội dung |
|---|---|
| `DocScanner` | App MAUI (Android): UI, ViewModel, tích hợp camera/AdMob/Play Billing. |
| `DocScanner.Core` | Logic không phụ thuộc platform (net9.0, test được trên PC): mô hình tài liệu, lưu trữ, pipeline nhập/xuất. |
| `DocScanner.Shared` | Thuật toán ảnh thuần managed (net9.0): dò mép giấy, nắn phối cảnh, Sauvola/Otsu, PNG writer... (namespace vẫn `ImageCoreService` vì lịch sử, xem CLAUDE.md). |
| `DocScanner.Core.Tests` | Unit test xUnit cho `DocScanner.Core` (và gián tiếp `DocScanner.Shared` qua test tích hợp). |
| `DocScanner.Shared.Tests` | Unit test xUnit riêng cho `DocScanner.Shared` (dò mép giấy qua cảnh giả lập, nắn phối cảnh, Sauvola, PngWriter...). |

## Build & test

```bash
dotnet test DocScanner.Core.Tests/DocScanner.Core.Tests.csproj       # 196 test, không cần Android SDK
dotnet test DocScanner.Shared.Tests/DocScanner.Shared.Tests.csproj   # 135 test, không cần Android SDK
dotnet build DocScanner/DocScanner.csproj -f net10.0-android         # cần workload "android" (dotnet workload install android)
dotnet build DocScanner/DocScanner.csproj -f net10.0-android -t:Run  # cài + chạy trên máy/máy ảo đã kết nối (adb devices)
```

Build Release/AAB để upload Play Console: xem [DocScanner/Build_aab.md](DocScanner/Build_aab.md).

## Trạng thái hiện tại (kiểm tra lại 2026-10-06, sau khi tách repo)

- Build Debug (Android) sạch, 0 lỗi. `DocScanner.Core.Tests` 196/196 PASS + `DocScanner.Shared.Tests` 135/135 PASS
  (= 331 test tổng, cả hai project đã đăng ký trong `DocScanner/DocScanner.slnx`).
- Đã sửa 2 lỗi do tách repo:
  - `DocScanner.Core.csproj` còn tham chiếu dự án ra ngoài repo (thư mục monorepo cũ) -- đã xoá, chỉ còn tham chiếu nội bộ.
  - `DocScanner.Shared.Tests` (owner bổ sung lại 2026-10-06) thiếu `ProjectReference` tới `DocScanner.Shared` nên
    không build được -- đã thêm lại.
- **Mất**: `MOBILE-STATUS.md` (tổng hợp việc chưa kiểm chứng/còn lại) và `THIRD-PARTY-NOTICES.md` (danh sách license
  bên thứ ba) được nhắc nhiều lần trong các file `.md` khác nhưng không có trong repo, không khôi phục được.
- Android Release build chưa được build lại/cài thử trên máy sau các sửa gần nhất (xem mục cuối `CLAUDE.md`, đợt
  2026-10-05) -- cần owner tự build + thử trên máy thật trước khi tin tưởng hoàn toàn.

Xem [LICENSE-MONETIZATION.md](LICENSE-MONETIZATION.md) cho mô hình kiếm tiền (ads + Pro bỏ quảng cáo) và bản quyền
thành phần bên thứ ba, [Guid-upload.md](Guid-upload.md) cho việc còn lại trước khi phát hành lên Google Play.
