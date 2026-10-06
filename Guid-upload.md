> Cập nhật 2026-10-06: Phần A (3 mã AdMob) đã xong từ 2026-09-29 -- xem CLAUDE.md, app đang chạy quảng cáo thật. Mô
> hình trả phí ở phần B cũng đã đổi (bỏ giới hạn 5 lần xuất PDF, giờ chỉ quảng cáo xen kẽ sau mỗi 5 lần) -- xem
> LICENSE-MONETIZATION.md mục 4. Phần B (đăng Play Console) có vẻ vẫn còn việc chưa xong, giữ nguyên bên dưới.

A. Quảng cáo AdMob — đang chờ đúng 3 mã để hoàn tất
Đã xong trong code: banner ở màn chính, quảng cáo toàn màn hình sau mỗi 5 lần xuất PDF, tự tắt hết khi mua Pro, dùng quảng cáo thử nghiệm an toàn khi chưa có mã thật (kể cả bản Release) nên test được ngay mà không sợ tài khoản AdMob bị phạt. 199 test PASS, đã cài + chạy ổn định trên Note 10+ hôm qua.

① Việc anh tự làm (cần tài khoản AdMob riêng, tôi không làm thay được):

Tạo tài khoản tại admob.google.com.
Thêm app "Doc Scanner" (Android, chọn "Không có trên cửa hàng" vì chưa lên Play).
Tạo 2 đơn vị quảng cáo: 1 Banner + 1 Interstitial.
Lấy 3 mã: App ID + 2 Ad unit ID.
Đăng ký máy Note 10+ làm test device trong AdMob (Settings → Test devices) — tránh bị tính gian lận click khi lỡ bấm quảng cáo thật trên máy mình.
② Việc tôi làm tiếp ngay khi có 3 mã ở bước 4:

Điền vào DocScanner.csproj (App ID) và AdsConfig.cs (2 ad unit ID).
Build + cài lại bản Release, xác nhận quảng cáo thật hiện đúng (không còn khung "Test Ad").
B. Đăng lên Google Play — chưa bắt đầu phần chính thức
① Việc tôi làm được ngay, không cần chờ anh:

Tạo keystore ký bản Release + đặt versionCode/versionName + build ra file AAB (hiện code chưa có cấu hình ký, đây là việc kỹ thuật thuần tuý).
② Việc anh tự làm (cần tài khoản Play Console riêng, phí 25 USD một lần, không hoàn lại):

Tạo app trên Play Console.
Tạo sản phẩm managed đúng ID pro_upgrade (khớp code), đặt giá, thêm chính mình làm license tester để mua thử không mất tiền thật.
Khai Data safety: app có dùng Internet (tự cập nhật, Play Billing, AdMob) nhưng ảnh/tài liệu không rời máy — chỉ trạng thái mua hàng và dữ liệu quảng cáo/Advertising ID đi qua Play/Google. Khai rõ "Có chứa quảng cáo".
Viết chính sách quyền riêng tư (privacy policy — cần 1 trang web/URL công khai, Play bắt buộc có mục này khi có quảng cáo), ảnh chụp màn hình, mô tả app.
Sau khi sản phẩm pro_upgrade được duyệt: tạo mã khuyến mãi ra mắt nếu muốn.
③ Trước khi công khai — cả hai cùng làm: thử trên ít nhất 5 máy Android khác hãng (Samsung/Xiaomi/Oppo...): mua Pro thật, xuất đủ 5 lượt PDF rồi bị chặn đúng lúc, đổi mã khuyến mãi, banner + interstitial hiện đúng, mua Pro xong quảng cáo biến mất ngay.

Thứ tự hợp lý nhất: làm xong phần A (AdMob) trước — nhanh, không tốn tiền, xác nhận quảng cáo thật hoạt động đúng — rồi mới sang phần B (tốn 25 USD, mất thời gian duyệt). Anh có mã AdMob chưa, hay cần tôi làm phần ký keystore/AAB trước trong lúc chờ?