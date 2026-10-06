# Bản quyền & mô hình thu tiền -- tóm tắt

Ghi lại cho owner (yêu cầu 2026-09-29): các điểm chính về bản quyền (của app, và các thành phần dùng trong app) và
cách app mobile `DocScanner` ("Doc Scanner", package `btk.docscanner`) tạo thu nhập hiện nay. Đây là bản tóm tắt kỹ
thuật để owner nắm nhanh, không phải ý kiến pháp lý -- các mục đánh dấu ⚠ nên nhờ luật sư xác nhận trước khi bán.

Chi tiết đầy đủ từng nằm ở hai file khác (`THIRD-PARTY-NOTICES.md`: danh sách đầy đủ thư viện + license + rủi ro bằng
sáng chế; `MOBILE-STATUS.md` mục 5k: chi tiết kỹ thuật cài đặt license Play Billing) -- **cả hai file đó hiện KHÔNG có
trong repo** (rà soát 2026-10-06, không tìm thấy ở repo cũ cũng như lịch sử git để khôi phục). Bản tóm tắt dưới đây là
tất cả những gì còn lại.

## 1. App là của ai

- Tác giả đứng tên trong app: **Nguyễn Hữu Quỳnh**, © 2026, "Mọi quyền được bảo lưu" (ghi trong màn Thông tin ứng
  dụng và màn khởi động của app). Liên hệ hiển thị trong app: `nguyenquynhvp.ictu@gmail.com`, `0988 632 841`.
- App **cố ý không liên quan tới công ty** (không dùng tên/thương hiệu IMIP) -- đây là sản phẩm cá nhân, bán độc
  lập qua tài khoản Google Play Console riêng của owner.
- Thuật toán xử lý ảnh chính (dò tờ giấy, nắn phối cảnh, đen trắng, làm nét, chữ ký...) là code owner/Claude tự viết
  từ đầu -- không phải bọc lại thư viện xử lý ảnh thương mại nào, nên không vướng license của bên thứ ba ở phần lõi.

## 2. Bản quyền các thành phần dùng trong app (bên thứ ba)

Đã rà soát toàn bộ thư viện, dữ liệu (font...), công cụ build dùng trong app mobile `DocScanner` -- bảng đầy đủ từng ở
`THIRD-PARTY-NOTICES.md` (file này hiện không có trong repo, xem mục 6). Tóm tắt:

- **Không có thành phần GPL / LGPL / AGPL** trong app đã phát hành -- toàn bộ là MIT, Apache-2.0, hoặc SIL OFL, đều
  cho phép dùng và bán thương mại, chỉ cần giữ lại thông báo bản quyền.
- App mobile `DocScanner` (cái đang bán): .NET MAUI, CommunityToolkit.Mvvm, AndroidX CameraX, Material Icons,
  Open Sans, Plugin.InAppBilling -- tất cả MIT / Apache-2.0 / SIL OFL, không có gì cần trả phí license hay xin phép
  riêng để bán.
- **JBIG2 đã bị gỡ bỏ hoàn toàn khỏi app** (quyết định của owner, 2026-09-29): không còn code, không còn file
  `jbig2.exe` vendor, không còn tuỳ chọn trong Cài đặt -- để không phải theo dõi nguồn gốc bản build / bằng sáng chế
  lịch sử của JBIG2 nữa. Trang trắng đen giờ luôn dùng **CCITT G4** (bằng sáng chế đã hết hạn từ lâu, không rủi ro).
  CCITT G4 và JPEG baseline hoàn toàn hết hạn bằng sáng chế, dùng thoải mái.

## 3. Bản quyền nội dung người dùng quét (điều khoản trong app)

App **không giữ bản quyền nội dung khách quét** -- tài liệu, chữ ký, ảnh là của người dùng, chỉ lưu trên máy họ.
Màn "Thông tin ứng dụng" trong app đã có điều khoản sử dụng nói rõ:
- Người dùng tự chịu trách nhiệm về nội dung mình quét/ký/chia sẻ và phải tuân thủ pháp luật về bản quyền, bí mật,
  dữ liệu cá nhân của chính nội dung đó (app chỉ là công cụ).
- App cung cấp "theo hiện trạng", tác giả không chịu trách nhiệm thiệt hại do mất dữ liệu / sai lệch nội dung.
- Chữ ký đặt trên trang là **ảnh chữ ký tay**, không phải chữ ký số hợp lệ theo quy định giao dịch điện tử -- cần
  nói rõ với khách nếu họ định dùng cho hợp đồng/văn bản cần giá trị pháp lý.
- Cấm sao chép / dịch ngược / sửa đổi / phân phối lại app khi chưa có đồng ý bằng văn bản của tác giả (bảo vệ bản
  quyền chính app).
- Riêng tư: ảnh/tài liệu/PDF chỉ lưu trên máy, không gửi đi đâu trừ khi người dùng tự bấm Chia sẻ; app không có
  quảng cáo, không thu thập dữ liệu dùng để bán/phân tích.

## 4. Cách app hiện tạo thu nhập

> ⚠ **Mục này đã CŨ (mô hình chốt 28/09).** Theo nhật ký `CLAUDE.md` (đợt 2026-10-04), owner đã tự bỏ hẳn paywall/giới
> hạn 5 lượt xuất PDF ở dưới: giờ xuất PDF **miễn phí không giới hạn**, chỉ hiện **quảng cáo xen kẽ (interstitial) sau
> mỗi 5 lần xuất** (`AdsPolicy.ExportsPerInterstitial = 5`); mua Pro = tắt mọi quảng cáo (banner + interstitial), không
> còn mở khoá "xuất không giới hạn" như mô tả dưới đây vì xuất đã luôn không giới hạn. Giữ lại nguyên văn bên dưới để
> có lịch sử, nhưng đừng coi đây là mô hình đang chạy thật.

**Mô hình đã chốt với owner (28/09, ĐÃ THAY ĐỔI -- xem cảnh báo trên)**: bán duy nhất qua **Google Play**, kiểu **mua
đứt một lần** (không phải thuê bao), thanh toán qua **Google Play Billing** -- không có máy chủ license riêng, không
bán trực tiếp ngoài Play.

- **Dùng thử miễn phí**: **5 lượt xuất PDF** đầu tiên miễn phí, không giới hạn theo ngày. Chụp ảnh, dò mép, chỉnh
  sửa, xem trước -- dùng thoải mái không giới hạn ở mọi lượt; chỉ chặn đúng lúc xuất PDF (lúc khách nhận thành phẩm),
  để khách trải nghiệm đủ trước khi bị mời mua.
- **Gói trả phí**: **"Pro"**, sản phẩm Play Console tên `doc_scanner_pro_upgrade_guidid_20260930_1131_101_01051989`
  (owner tự đặt, đã điền vào code đợt 2026-09-29 tối), kiểu **managed / mua một lần** (không tiêu hao, không gia
  hạn) -- mua xong dùng vĩnh viễn, xuất PDF không giới hạn.
- **Play là "máy chủ license"**: app chỉ hỏi Google Play "tài khoản này đã mua gói Pro chưa" mỗi lần mở app /
  mở Cài đặt -- không có license key, không server riêng. Khách bị hoàn tiền / Google thu hồi giao dịch thì lần hỏi
  tiếp theo Play tự trả lời "chưa mua", app tự khoá lại, không cần owner làm gì.
- **Giá bán**: do **owner tự đặt trong Play Console**. Code đã có đúng ID sản phẩm; owner cần tự xác nhận đã **tạo
  sản phẩm managed với đúng ID này trên Play Console** và đặt giá, nếu chưa thì nút Mua Pro chưa hoạt động được
  với tiền thật.
- **Khuyến mãi ra mắt**: dùng thẳng **mã giảm giá (Promo codes) có sẵn trong Play Console** (Kiếm tiền > Sản phẩm >
  chọn sản phẩm Pro > Khuyến mãi) -- owner tự tạo mã, đặt % giảm hoặc miễn phí, hạn dùng, rồi phát cho khách (Facebook,
  Zalo...). Khách đổi mã ngay trong **app Google Play Store** (không phải trong Doc Scanner); Play tự ghi nhận đã
  mua, app tự nhận ra ở lần mở tiếp theo -- **không cần owner báo code gì cho app biết**.
- **Play thu phí dịch vụ**: Google Play luôn giữ lại một phần doanh thu mỗi giao dịch bán hàng trong app theo chính
  sách phí dịch vụ hiện hành của họ (mức phí có thể khác nhau tuỳ loại giao dịch / doanh thu năm / chương trình đăng
  ký nhà phát triển) -- owner nên xem đúng mức phí áp dụng cho tài khoản của mình trong Play Console trước khi đặt
  giá, vì số liệu này Google có thể thay đổi theo thời gian.

### Owner cần tự làm trên Play Console để bắt đầu có thu nhập thật (không làm thay được, cần tài khoản riêng của owner)

1. Tạo app trên Play Console (phí đăng ký nhà phát triển một lần theo chính sách Google hiện hành), điền Data
   safety (app hiện CÓ dùng Internet: Play Billing + AdMob -- không còn tự cập nhật, tính năng đó đã gỡ bỏ đợt
   2026-09-29 vì Play tự cập nhật; ảnh/tài liệu vẫn không rời máy), chính sách quyền riêng tư, ảnh chụp màn hình,
   mô tả app.
2. Tạo sản phẩm **managed** đúng ID hiện có trong code (xem `LicenseService.ProProductId`; phải khớp chính xác
   từng ký tự), đặt giá bán.
3. Thêm tài khoản Gmail của owner làm **license tester** để bấm Mua Pro thử không mất tiền thật, kiểm tra toàn bộ
   luồng trước khi phát hành thật.
4. Sau khi sản phẩm được duyệt: tạo mã khuyến mãi ra mắt nếu muốn, phát hành app lên Play. Keystore ký bản Release đã
   được tạo (`DocScanner/release/`, xem `KEYSTORE-README.md`); AAB đã build/ký thành công nhiều lần (xem `CLAUDE.md`).

### Chưa kiểm chứng được (chỉ kiểm chứng được sau khi có sản phẩm thật trên Play Console)

Mua Pro thật, việc xác nhận giao dịch (acknowledge) đúng, hộp thoại chặn xuất PDF khi hết lượt hoạt động đúng trên
giao dịch thật (**lưu ý: cơ chế chặn này đã bị owner tắt, xem cảnh báo ở mục 4**), đổi mã khuyến mãi trên Play Store
rồi app tự nhận Pro, khôi phục giao dịch trên máy thứ hai cùng tài khoản Google, giá hiển thị đúng theo khu vực/tiền
tệ. (Chi tiết kỹ thuật từng ở `MOBILE-STATUS.md` mục 5k -- file đó không có trong repo, xem mục 6.)

## 5. Nếu sau này owner muốn bán thêm kênh ngoài Play (website, chuyển khoản...)

Kiến trúc hiện tại đã tách `ILicenseService` thành một giao diện riêng (`DocScanner.Core/Licensing/`) -- có thể viết
thêm một bản cài đặt khác dùng máy chủ license riêng (lúc đó mới thật sự cần server) mà không phải đổi phần còn lại
của app. Hiện tại **chưa cần và chưa có** máy chủ license riêng, vì chỉ bán qua Play.

## 6. File tham chiếu bị mất (rà soát 2026-10-06)

`THIRD-PARTY-NOTICES.md` và `MOBILE-STATUS.md`, được nhắc tới nhiều lần ở trên, không có trong repo này, không có ở
repo monorepo cũ, và không có lịch sử git để khôi phục -- coi như đã mất nội dung. Nếu cần danh sách license bên thứ
ba để nộp lên Play Console (mục khai "Data safety"/license), phải viết lại từ đầu dựa trên `DocScanner/DocScanner.csproj`
(danh sách `PackageReference` + comment license cạnh mỗi dòng).
