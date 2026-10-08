Build file .aab cho DocScanner

⚠ Trước khi build, nếu đã lâu không build Release, hoặc vừa đổi cấu hình project/thêm dependency mới: xoá sạch
`DocScanner/obj/Release` và `DocScanner/bin/Release` trước. Gặp đúng 1 lần (2026-10-08): cache `obj/Release` cũ khiến
font icon (`MaterialIcons-Regular.ttf`) bị rơi mất khỏi bản Release dù source/csproj đều đúng và bản Debug vẫn bình
thường — icon tự hiện chữ Hán (font dự phòng trùng mã Unicode). Xoá 2 thư mục trên rồi build lại là hết, không phải
sửa code gì. Chi tiết: CLAUDE.md đợt 2026-10-08 ("Icon hiện chữ Hán trên bản Release").

Lệnh (chạy ở thư mục gốc repo, PowerShell hoặc terminal):

```bash
dotnet publish DocScanner/DocScanner.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=aab
```

File kết quả nằm ở:
DocScanner/bin/Release/net10.0-android/publish/btk.docscanner-Signed.aab

Vì sao không cần truyền mật khẩu keystore
DocScanner.csproj tự <Import Project="release\Signing.props" ...> khi build ở cấu hình Release và file đó tồn tại — nó đã có sẵn từ đợt trước (DocScanner/release/Signing.props + docscanner-upload.keystore), nên lệnh publish ở trên tự ký đúng bằng upload key cũ, không cần nhập gì thêm.

Trước khi build, nhớ tăng version (bắt buộc với mỗi bản upload lên Play)
Sửa trong DocScanner.csproj (hiện ở dòng 37-38, hiện đang là 1.0 / 2 -- số dòng có thể lệch sau mỗi sửa file, tìm theo tên thuộc tính):


<ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>  <!-- số hiển thị cho người dùng -->
<ApplicationVersion>2</ApplicationVersion>                   <!-- versionCode, PHẢI tăng mỗi lần -->
Play Console từ chối thẳng nếu ApplicationVersion (versionCode) không lớn hơn bản đã có.

Kiểm tra chữ ký sau khi build (tuỳ chọn, để chắc ăn trước khi upload)

```bash
jarsigner -verify "DocScanner/bin/Release/net10.0-android/publish/btk.docscanner-Signed.aab"
```


Phải thấy jar verified.