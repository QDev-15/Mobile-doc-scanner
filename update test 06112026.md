# Doc Scanner — Ghi chú xử lý "Apply for production" bị từ chối (06/11/2026)

## 1. Tình huống ban đầu

Google Play báo: *"Your app requires more testing to access Google Play production"* sau khi bấm Apply for production. Checklist hiện ra:
- ✅ Publish a closed testing release
- ✅ Have at least 12 testers opted-in
- ❌ **Run your closed test with at least 12 testers for 14 more days, starting from the review date** (review date: Thứ Năm, 06/11/2026, 4:29 AM)

### Vì sao bị yêu cầu test thêm dù đã đủ 12 tester/14 ngày trước đó
Mốc "14 ngày" **không cộng dồn** — mỗi lần Google review và từ chối, đồng hồ **reset lại từ ngày review đó**. Checklist số (12 tester/14 ngày) chỉ là điều kiện tối thiểu để được nộp đơn, không đảm bảo được duyệt — Google còn xét thêm mức độ tương tác thật của tester, crash rate, và tính chính xác của khai báo chính sách/dữ liệu.

Nếu không sửa gì mà chỉ chờ đủ 14 ngày rồi apply lại, khả năng cao **vẫn bị từ chối tiếp** vì vấn đề gốc (nếu có) chưa được giải quyết.

### Yếu tố rủi ro ở cấp tài khoản (không sửa được bằng code)
Tài khoản **AdMob** gắn với cùng nhà phát triển này **đã bị Google đóng 2 lần** (2026-10-08 và 2026-10-14, kháng cáo chưa có phản hồi) — buộc phải chuyển quảng cáo sang AppLovin (bị từ chối nhận publisher mới) rồi sang **Unity LevelPlay**. Play Console và AdMob thường gắn chung tài khoản Google, nên rất có khả năng hệ thống trust-and-safety của Google đang áp mức soi xét cao hơn cho tài khoản Play Console này.

---

## 2. Review code DocScanner — các vấn đề tìm thấy

### Vấn đề nghiêm trọng: nội dung "Quyền riêng tư" trong app SAI so với thực tế (ĐÃ SỬA)
`AboutPage.xaml` từng ghi *"không thu thập dữ liệu sử dụng"*, nhưng thực tế app:
- Khai báo quyền `com.google.android.gms.permission.AD_ID`
- Chạy SDK quảng cáo Unity LevelPlay/ironSource (có cả `ACCESS_ADSERVICES_ATTRIBUTION`, `ACCESS_ADSERVICES_TOPICS` trong manifest gộp)

→ Mâu thuẫn trực tiếp với Google Play User Data policy.

### Thiếu Privacy Policy URL hosted public
Không có link tới trang privacy policy bên ngoài ở bất kỳ đâu trong code/Play Console (trước khi sửa) — Play Console bắt buộc mục này.

### Thiếu GDPR/UMP consent flow
`MauiProgram.cs` gọi `UseLevelPlayAds(...)` ngay khi khởi động, không qua bước xin consent (CMP/UMP) cho người dùng EEA/UK. **Chưa triển khai** — cần quyết định riêng nếu muốn thêm (việc lớn hơn, chưa làm trong đợt này).

---

## 3. Các thay đổi đã thực hiện

### Code (`DocScanner/Views/AboutPage.xaml` và `AboutPage.xaml.cs`)
- Sửa đoạn "QUYỀN RIÊNG TƯ": nêu đúng 3 việc dùng mạng — (1) quảng cáo qua **Unity LevelPlay** (không còn ghi sai là AdMob) thu thập Mã quảng cáo/dữ liệu thiết bị/dữ liệu tương tác quảng cáo; (2) Google Play Billing (xác thực mua Pro); (3) Google Play (kiểm tra cập nhật).
- Thêm hằng số `PrivacyPolicyUrl` và dòng link bấm được **"Xem chính sách quyền riêng tư đầy đủ"** → mở `https://sites.google.com/view/docscanne/home` qua `Launcher.Default.OpenAsync`.
- **Lưu ý:** app không tự tải nội dung từ site — chỉ mở trình duyệt ngoài. Text trong app và nội dung site là 2 nơi tách biệt, phải tự tay giữ đồng bộ khi sửa.

### Website (`https://sites.google.com/view/docscanne/home`)
Bản HTML đã sửa lưu tại `DocScanner/docscanner-privacy-policy.html` (file tham khảo, không bị build vào app — `MauiAsset` chỉ quét `Resources\Raw\**`). Các lỗi đã sửa so với bản gốc:
- **"Google AdMob" → "Unity LevelPlay (mediation quảng cáo, gồm ironSource và các đối tác quảng cáo trong chuỗi mediation)"** — khớp đúng SDK thật, tránh mismatch khi Google quét APK đối chiếu với Data safety.
- **Bỏ câu claim sai** về hộp thoại UMP/consent (code chưa có cơ chế này thật).
- **Thêm link chính sách của Unity Technologies** (`https://unity.com/legal/privacy-policy`) bên cạnh link Google — vì Unity mới là bên kiểm soát dữ liệu cho dòng quảng cáo, không phải Google.
- Cập nhật ngày "Cập nhật lần cuối" → 06/11/2026.

**Việc còn phải tự làm:** copy nội dung file HTML này vào Google Sites (Google Sites không nhận upload file .html trực tiếp).

### Tuỳ chọn chưa làm (optional, cân nhắc sau)
- Bản tiếng Anh của privacy policy (site gốc có toggle Tiếng Việt/English, bản mới chỉ có tiếng Việt).
- Nhắc tới Privacy Sandbox API (Attribution, Topics) trong Mục 4 của policy cho đầy đủ hơn.
- Thêm consent flow (UMP/CMP) thật vào app cho người dùng EEA/UK.

---

## 4. Checklist cần làm trên Play Console

Vị trí menu thực tế (giao diện Play Console hiện tại của tài khoản này): **Policy** là nhóm riêng, nằm **dưới "Monetize with Play"** trong sidebar (không nằm trong "Grow users" như đoán ban đầu — đã xác nhận "Grow users" chỉ có Store presence/Store performance/Translations/Deep links).

- [ ] **Policy → Privacy policy**: dán link `https://sites.google.com/view/docscanne/home`
- [ ] **Policy → Data safety**: khai **Device or other IDs** (Advertising ID) có thu thập, mục đích *Advertising or marketing*; khai bên thứ ba nhận dữ liệu là Unity Technologies/ironSource
- [ ] **Policy → Ads**: chọn **"Yes, my app contains ads"**
- [ ] **Policy → Advertising ID declaration** (nếu có form riêng): xác nhận Yes, mục đích Advertising
- [ ] **Policy status**: kiểm tra có cảnh báo/vi phạm nào đang treo trên tài khoản không (liên quan lịch sử AdMob bị đóng)
- [ ] **Monitor and improve → Android vitals**: kiểm tra crash rate / ANR rate
- [ ] **Monitor and improve → Pre-launch reports**: xem Google có tự phát hiện lỗi nào không
- [ ] **Test and release → Production**: xác nhận bản production cuối cùng (sau này) build đúng `-p:AdsMode=Pro`
- [ ] **Grow users → Store presence → Store listings**: screenshot/mô tả khớp đúng tính năng thật, không placeholder
- [ ] **Protected with Play**: xác nhận App signing không có cảnh báo

---

## 5. Quy trình build & upload (nhắc lại cho rõ, tránh nhầm)

| Giai đoạn | Track | Build | Lý do |
|---|---|---|---|
| **Hiện tại** — đang test bắt buộc | Closed testing | **Dev** (mặc định, KHÔNG truyền `-p:AdsMode=Pro`) → ads tắt hẳn | Tránh tester lỡ bấm trúng ads thật — rủi ro y hệt vụ AdMob từng bị cấm |
| **Sau khi được duyệt production** | Production | **Pro** (`-p:AdsMode=Pro`) → ads thật | Người dùng thật, cần doanh thu quảng cáo |

Lệnh build bản Dev (dùng ngay bây giờ cho Closed testing):
```
dotnet publish DocScanner/DocScanner.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=aab
```

Upload bản mới (có fix AboutPage.xaml) lên **đúng track Closed testing đang chạy** — KHÔNG tạo track mới, KHÔNG đổi track. Đẩy build mới lên track cũ **không làm reset đồng hồ 14 ngày** (mốc đó tính theo thời gian tester opt-in liên tục, không tính theo phiên bản build).

---

## 6. Trình tự các bước tiếp theo

1. Đẩy bản Dev đã fix AboutPage.xaml lên track Closed testing hiện tại.
2. Cập nhật đồng thời: Privacy policy URL, Data safety, Ads declaration trên Play Console (không cần build mới cho việc này).
3. Kiểm tra Policy status, Android vitals, Pre-launch reports — sửa nếu có vấn đề.
4. Giữ đủ **12 tester liên tục trong 14 ngày**, tính từ 06/11/2026.
5. Sau 14 ngày → bấm lại **"Apply for production"**.
6. Nếu được duyệt → build bản **Pro** (`-p:AdsMode=Pro`) → upload lên track **Production**.
