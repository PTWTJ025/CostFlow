# ฟีเจอร์ที่ 2 — Export รายงานเป็น Excel

## ทำอะไร
ให้ผู้ใช้กดดาวน์โหลดข้อมูลออกมาเป็นไฟล์ Excel (.xlsx) ได้ทั้งจากหน้ารายงานและหน้ารายการสั่งซื้อ

## ไฟล์ที่เกี่ยวข้อง

| ไฟล์ | หน้าที่ |
|---|---|
| `Controllers/ReportController.cs` | Export รายงานเปรียบเทียบไฟล์ |
| `Controllers/ProductSearchController.cs` | Export รายการสั่งซื้ออะไหล่ |
| `Views/Report/Details.cshtml` | ปุ่ม Export ในหน้ารายงาน |
| `Views/ProductSearch/SavedOrders.cshtml` | ปุ่ม Export ในหน้ารายการที่บันทึก |

> ระบบใช้ Library **ClosedXML** สำหรับสร้างไฟล์ Excel ครับ

---

## ขั้นตอนการทำงาน (Flow)

```
ผู้ใช้กดปุ่ม "Export Excel"
        ↓
Controller ดึงข้อมูลจากฐานข้อมูล
        ↓
สร้าง XLWorkbook ใหม่ด้วย ClosedXML
เพิ่มหัวตาราง (Header Row) ลงในชีต
วนลูปข้อมูลใส่ทีละแถว
ตกแต่งสีหัวตาราง ปรับความกว้างคอลัมน์
        ↓
บันทึกไฟล์ลง MemoryStream
ส่งกลับเป็น FileContentResult ให้เบราว์เซอร์ดาวน์โหลด
```

---

## ฟังก์ชันหลักใน Controller

### `ReportController.ExportExcel(Guid sessionId)`
- ดึงข้อมูลจากตาราง `MergeResults` ตาม sessionId
- สร้างไฟล์ Excel มีคอลัมน์: เลข PO, ชื่ออะไหล่, จำนวน, ราคา, ส่วนต่าง
- ส่งไฟล์กลับในชื่อ `รายงาน_<ชื่อไฟล์>_<วันที่>.xlsx`

### `ProductSearchController.ExportBatchExcel(Guid batchId)`
- ดึงข้อมูลจากตาราง `SparePartOrders` ตาม batchId
- สร้างไฟล์ Excel มีคอลัมน์: รหัสสินค้า, ชื่ออะไหล่, หน่วย, ราคา, จำนวน, รวม
- ส่งไฟล์กลับในชื่อ `ใบสั่งซื้อ_<ชื่อ Batch>_<วันที่>.xlsx`

---

## ข้อควรรู้

- ไฟล์จะถูกสร้างใน **Memory** และส่งตรงไปยังเบราว์เซอร์ ไม่บันทึกลงเซิร์ฟเวอร์
- ถ้าแก้ไขหัวตาราง ให้แก้ได้ตรงใน Controller function ที่เกี่ยวข้องได้เลยครับ
- ไลบรารีที่ใช้: `ClosedXML` เวอร์ชัน `0.105.0` (ดูใน `CostFlow.csproj`)
