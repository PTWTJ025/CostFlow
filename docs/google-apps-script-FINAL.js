function doPost(e) {
  try {
    var data = JSON.parse(e.postData.contents);
    var ss = SpreadsheetApp.getActiveSpreadsheet();

    var resultPlans = 0;
    if (data.PlanGroups || data.planRows || data.SheetName_Plans) {
      resultPlans = processSection(
        ss,
        data.SheetName_Plans || "แผนผลิตประจำสัปดาห์",
        data.PlanGroups,
        data.planRows,
        getHeadersPlan(),
        "plan",
      );
    }

    var resultActions = 0;
    if (data.ActionGroups || data.actionRows || data.SheetName_Actions) {
      resultActions = processSection(
        ss,
        data.SheetName_Actions || "บันทึกการรับของประจำเดือน",
        data.ActionGroups,
        data.actionRows,
        getHeadersAction(),
        "action",
      );
    }

    return ContentService.createTextOutput(
      JSON.stringify({
        success: true,
        archivedPlans: resultPlans,
        archivedActions: resultActions,
        message: "บันทึกข้อมูลและจัดรูปแบบตามเงื่อนไขเรียบร้อยแล้ว",
      }),
    ).setMimeType(ContentService.MimeType.JSON);
  } catch (err) {
    return ContentService.createTextOutput(
      JSON.stringify({
        success: false,
        error: err.toString(),
      }),
    ).setMimeType(ContentService.MimeType.JSON);
  }
}

function processSection(ss, sheetName, groups, fallbackRows, headers, type) {
  var sheet = ss.getSheetByName(sheetName);
  if (!sheet) {
    sheet = ss.insertSheet(sheetName);
  }

  var totalSaved = 0;

  if (groups && groups.length > 0) {
    for (var i = 0; i < groups.length; i++) {
      var grp = groups[i];
      if (grp.Rows && grp.Rows.length > 0) {
        saveOrUpdateMonthBlock(sheet, grp.MonthYear, headers, grp.Rows, type);
        totalSaved += grp.Rows.length;
      }
    }
  } else if (fallbackRows && fallbackRows.length > 0) {
    saveOrUpdateMonthBlock(sheet, "ข้อมูลสำรอง", headers, fallbackRows, type);
    totalSaved += fallbackRows.length;
  }

  applyColumnWidths(sheet, type);
  return totalSaved;
}

function saveOrUpdateMonthBlock(sheet, monthYearKey, headers, rows, type) {
  if (!rows || rows.length === 0) return;

  var numCols;
  if (type === "action") {
    headers = getHeadersAction();
    numCols = 9; // 9 คอลัมน์
  } else {
    headers = getHeadersPlan();
    numCols = headers.length;
  }

  var monthTitle = formatThaiMonthTitle(monthYearKey);
  var lastRow = sheet.getLastRow();

  // ─── ค้นหาว่ามี Block เดือนนี้อยู่แล้วหรือไม่ ──────────────────
  var existingStartRow = -1;
  var existingEndRow = -1;

  if (lastRow > 0) {
    var colA = sheet.getRange(1, 1, lastRow, 1).getValues();
    for (var r = 0; r < colA.length; r++) {
      var val = String(colA[r][0]).trim();
      if (val === monthTitle || val.indexOf(monthTitle) !== -1) {
        existingStartRow = r + 1; // 1-indexed
        break;
      }
    }
  }

  // ถ้าเจอ Block เดิม ให้หาจุดสิ้นสุดของ Block แล้วลบเฉพาะ Block นั้นออก
  if (existingStartRow !== -1) {
    existingEndRow = lastRow;
    var allColA = sheet
      .getRange(existingStartRow + 1, 1, lastRow - existingStartRow, 1)
      .getValues();
    for (var r = 0; r < allColA.length; r++) {
      var cellText = String(allColA[r][0]).trim();
      if (cellText.indexOf("เดือน:") === 0) {
        existingEndRow = existingStartRow + 1 + r - 1;
        if (existingEndRow > existingStartRow) {
          var prevVal = String(
            sheet.getRange(existingEndRow, 1, 1, 1).getValue(),
          ).trim();
          if (prevVal === "") existingEndRow--;
        }
        break;
      }
    }

    var numRowsToDelete = existingEndRow - existingStartRow + 1;
    if (numRowsToDelete > 0) {
      sheet.deleteRows(existingStartRow, numRowsToDelete);
    }
  }

  // กำหนด startRow สำหรับเขียน Block ใหม่
  var updatedLastRow = sheet.getLastRow();
  var startRow;
  if (existingStartRow !== -1 && existingStartRow <= updatedLastRow + 1) {
    if (existingStartRow <= updatedLastRow) {
      sheet.insertRowsBefore(existingStartRow, rows.length + 3);
      startRow = existingStartRow;
    } else {
      startRow = updatedLastRow === 0 ? 1 : updatedLastRow + 2;
    }
  } else {
    startRow = updatedLastRow === 0 ? 1 : updatedLastRow + 2;
  }

  var myFont = "Chakra Petch";

  // ─── 1. หัวข้อเดือน ─────────────────────────────────────────────
  var titleRange = sheet.getRange(startRow, 1, 1, numCols);
  titleRange.merge();
  titleRange.setValue(monthTitle);
  titleRange.setFontFamily(myFont).setFontWeight("bold").setFontSize(11);
  titleRange.setFontColor("#000000").setBackground("#D9E1F2");
  titleRange.setHorizontalAlignment("center").setVerticalAlignment("middle");

  var headerRowIndex = startRow + 1;
  var dataStartRowIndex = headerRowIndex + 1;

  // ─── 2. Header คอลัมน์ ──────────────────────────────────────────
  var headerRange = sheet.getRange(headerRowIndex, 1, 1, numCols);
  headerRange.setValues([headers]);
  headerRange.setFontFamily(myFont).setFontWeight("bold").setFontSize(10);
  headerRange.setFontColor("#FFFFFF").setBackground("#4A86E8");
  headerRange.setHorizontalAlignment("center").setVerticalAlignment("middle");

  // ─── 3. เขียนข้อมูล ─────────────────────────────────────────────
  var formattedRows = rows.map(function (row) {
    var newRow = [];
    for (var c = 0; c < numCols; c++) {
      newRow.push(row[c] !== undefined ? row[c] : "");
    }
    return newRow;
  });

  var dataRange = sheet.getRange(
    dataStartRowIndex,
    1,
    formattedRows.length,
    numCols,
  );
  dataRange.setValues(formattedRows);
  dataRange.setFontFamily(myFont).setFontSize(10).setFontColor("#000000");
  dataRange.setVerticalAlignment("middle").setWrap(true);

  // ─── 4. จัด Alignment ──────────────────────────────────────────
  if (type === "plan") {
    sheet
      .getRange(dataStartRowIndex, 1, rows.length, 4)
      .setHorizontalAlignment("center");
    sheet
      .getRange(dataStartRowIndex, 5, rows.length, 1)
      .setHorizontalAlignment("left");
    sheet
      .getRange(dataStartRowIndex, 6, rows.length, 4)
      .setHorizontalAlignment("center");
  } else {
    // 9 คอลัมน์ของ Action Sheet
    sheet
      .getRange(dataStartRowIndex, 1, rows.length, 3)
      .setHorizontalAlignment("center"); // A-C
    sheet
      .getRange(dataStartRowIndex, 4, rows.length, 1)
      .setHorizontalAlignment("left"); // D ชื่อสินค้า
    sheet
      .getRange(dataStartRowIndex, 5, rows.length, 2)
      .setHorizontalAlignment("center"); // E-F
    var amountRange = sheet.getRange(dataStartRowIndex, 7, rows.length, 1); // G ราคา
    amountRange.setHorizontalAlignment("right").setNumberFormat("#,##0.00");
    sheet
      .getRange(dataStartRowIndex, 8, rows.length, 2)
      .setHorizontalAlignment("center"); // H-I
  }

  // ─── 5. ไฮไลท์สีสถานะการรับของ (คอลัมน์ 8 = H) ─────────────────
  if (type === "action") {
    var statusRange = sheet.getRange(
      dataStartRowIndex,
      8,
      formattedRows.length,
      1,
    );
    var statuses = statusRange.getValues();
    var bgColors = [];
    for (var r = 0; r < statuses.length; r++) {
      var stat = String(statuses[r][0]).trim();
      if (stat === "รับสินค้าแล้ว") {
        bgColors.push(["#00FF00"]); // 🟢 เขียวสว่าง
      } else if (stat === "ผ่อนชำระ") {
        bgColors.push(["#FF9900"]); // 🟠 ส้มเหลือง
      } else {
        bgColors.push([null]); // ⚪ ไม่ใส่สี
      }
    }
    statusRange.setBackgrounds(bgColors);
  }

  // ─── 6. ตีเส้นขอบ ──────────────────────────────────────────────
  var fullTableRange = sheet.getRange(
    startRow,
    1,
    formattedRows.length + 2,
    numCols,
  );
  fullTableRange.setBorder(
    true,
    true,
    true,
    true,
    true,
    true,
    "#595959",
    SpreadsheetApp.BorderStyle.SOLID,
  );
}

function applyColumnWidths(sheet, type) {
  if (type === "plan") {
    sheet.setColumnWidth(1, 140);
    sheet.setColumnWidth(2, 160);
    sheet.setColumnWidth(3, 160);
    sheet.setColumnWidth(4, 130);
    sheet.setColumnWidth(5, 320);
    sheet.setColumnWidth(6, 110);
    sheet.setColumnWidth(7, 130);
    sheet.setColumnWidth(8, 140);
    sheet.setColumnWidth(9, 140);
  } else {
    // 9 คอลัมน์ของ Action Sheet
    sheet.setColumnWidth(1, 130); // A วัน/เวลาที่บันทึก
    sheet.setColumnWidth(2, 140); // B วันที่อนุมัติ
    sheet.setColumnWidth(3, 120); // C รหัสใบขออนุมัติ
    sheet.setColumnWidth(4, 280); // D ชื่อสินค้า
    sheet.setColumnWidth(5, 70); // E จำนวน
    sheet.setColumnWidth(6, 110); // F ปภ.ความเร่งด่วน
    sheet.setColumnWidth(7, 110); // G ราคายอดผลิต
    sheet.setColumnWidth(8, 120); // H สถานะการรับของ
    sheet.setColumnWidth(9, 130); // I ยกยอดมาจาก
  }
}

function formatThaiMonthTitle(key) {
  if (!key) return "เดือน: ไม่ระบุ";
  if (key.indexOf("-") !== -1) {
    var parts = key.split("-");
    var year = parseInt(parts[0], 10);
    var monthStr = parts[1].trim();
    if (monthStr.length > 2) monthStr = monthStr.substring(0, 2);
    var month = parseInt(monthStr, 10);
    if (!isNaN(year) && !isNaN(month)) {
      if (year < 2500) year += 543;
      var months = [
        "",
        "มกราคม",
        "กุมภาพันธ์",
        "มีนาคม",
        "เมษายน",
        "พฤษภาคม",
        "มิถุนายน",
        "กรกฎาคม",
        "สิงหาคม",
        "กันยายน",
        "ตุลาคม",
        "พฤศจิกายน",
        "ธันวาคม",
      ];
      var mName = months[month] || "เดือน " + month;
      return "เดือน: " + mName + " " + year;
    }
  }
  return "เดือน: " + key;
}

function getHeadersPlan() {
  return [
    "วันที่อัปโหลด",
    "วันที่อนุมัติ",
    "ชื่อไฟล์",
    "เลข PO",
    "ชื่อสินค้า",
    "แผนก",
    "กำหนดส่งมอบ",
    "สถานะในไฟล์แผน",
    "ผลการจับคู่",
  ];
}

function getHeadersAction() {
  return [
    "วัน/เวลาที่บันทึก", // A
    "วันที่อนุมัติ", // B
    "รหัสใบขออนุมัติ", // C
    "ชื่อสินค้า", // D
    "จำนวน", // E
    "ปภ.ความเร่งด่วน", // F
    "ราคายอดผลิต", // G
    "สถานะการรับของ", // H
    "ยกยอดมาจาก", // I (รวมทั้ง Deferred และ Skipped)
  ];
}
