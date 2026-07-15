// Weekly Plan Upload System - Multiple Files with Sheet Selection
// ระบบอัปโหลดแผนผลิตแบบหลายไฟล์พร้อมเลือก Sheet

const WeeklyPlanUploader = {
  uploadedFiles: [], // เก็บข้อมูล: [{sessionId, fileName, sheets, selectedSheet}]
  reportName: "",
  isProcessing: false,

  init(reportName) {
    this.reportName = reportName;
    this.setupEventListeners();
  },

  setupEventListeners() {
    const fileInput = document.getElementById("weeklyPlanFiles");
    const dropzone = document.getElementById("dropzone-area");
    const uploadBtn = document.getElementById("btn-upload-files");
    const processBtn = document.getElementById("btn-confirm-match");
    const backBtn = document.getElementById("btn-back-to-files");

    // Dropzone click
    dropzone.addEventListener("click", () => fileInput.click());

    // File input change
    fileInput.addEventListener("change", (e) =>
      this.handleFileSelection(e.target.files),
    );

    // Drag & Drop
    dropzone.addEventListener("dragover", (e) => {
      e.preventDefault();
      dropzone.classList.add("border-blue-400", "bg-blue-50/20");
    });

    dropzone.addEventListener("dragleave", () => {
      dropzone.classList.remove("border-blue-400", "bg-blue-50/20");
    });

    dropzone.addEventListener("drop", (e) => {
      e.preventDefault();
      dropzone.classList.remove("border-blue-400", "bg-blue-50/20");
      if (e.dataTransfer.files.length) {
        this.handleFileSelection(e.dataTransfer.files);
      }
    });

    // Upload button
    uploadBtn.addEventListener("click", () => {
      if (this.isProcessing) return;
      this.uploadFiles();
    });

    // Process button
    processBtn.addEventListener("click", () => {
      if (this.isProcessing) return;
      this.processWeeklyPlan();
    });

    // Back button
    backBtn.addEventListener("click", () => {
      if (this.isProcessing) return;
      this.backToUpload();
    });
  },

  handleFileSelection(files) {
    const filesArray = Array.from(files);

    if (filesArray.length === 0) {
      Swal.fire("คำแนะนำ", "กรุณาเลือกไฟล์", "warning");
      return;
    }

    if (filesArray.length > 10) {
      Swal.fire("คำแนะนำ", "อัปโหลดได้สูงสุด 10 ไฟล์เท่านั้น", "warning");
      return;
    }

    // แสดงรายชื่อไฟล์
    const filesList = document.getElementById("files-list");
    filesList.innerHTML = "";

    filesArray.forEach((file, index) => {
      const fileItem = document.createElement("div");
      fileItem.setAttribute(
        "style",
        "display: flex; align-items: center; gap: 12px; padding: 12px; background-color: #eff6ff; border: 1px solid #bfdbfe; border-radius: 6px; margin-bottom: 8px;",
      );
      fileItem.innerHTML = `
                <svg style="width: 20px !important; height: 20px !important; color: #2563eb !important; flex-shrink: 0;" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
                    <path stroke-linecap="round" stroke-linejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
                <span class="text-sm font-medium text-blue-800 truncate" style="flex: 1; min-width: 0;">${file.name}</span>
                <span class="text-xs text-blue-600" style="flex-shrink: 0;">${(file.size / 1024 / 1024).toFixed(2)} MB</span>
            `;
      filesList.appendChild(fileItem);
    });

    document.getElementById("btn-upload-files").classList.remove("hidden");
  },

  async uploadFiles() {
    const fileInput = document.getElementById("weeklyPlanFiles");
    const files = fileInput.files;

    if (files.length === 0) {
      Swal.fire("คำแนะนำ", "กรุณาเลือกไฟล์ก่อนอัปโหลด", "warning");
      return;
    }

    // Show loading
    Swal.fire({
      title: "กำลังอัปโหลดไฟล์...",
      html: "กรุณารอสักครู่",
      allowOutsideClick: false,
      didOpen: () => {
        Swal.showLoading();
      },
    });

    try {
      const formData = new FormData();
      Array.from(files).forEach((file) => {
        formData.append("files", file);
      });

      const response = await fetch("/FileMerge/UploadWeeklyPlanFiles", {
        method: "POST",
        body: formData,
      });

      const data = await response.json();

      if (data.success) {
        this.uploadedFiles = data.files;
        Swal.close();
        this.showSheetSelection();
      } else {
        Swal.fire("เกิดข้อผิดพลาด", data.error, "error");
      }
    } catch (error) {
      console.error("Upload error:", error);
      Swal.fire("เกิดข้อผิดพลาด", "ไม่สามารถอัปโหลดไฟล์ได้", "error");
    }
  },

  showSheetSelection() {
    document.getElementById("upload-step").classList.add("hidden");
    document.getElementById("sheet-selection-step").classList.remove("hidden");

    // Expand modal width to fit the registry list layout
    const modalContainer = document.getElementById("upload-modal-container");
    if (modalContainer) {
      modalContainer.classList.remove("max-w-lg", "max-w-4xl");
      modalContainer.classList.add("modal-container-expanded");
    }

    const container = document.getElementById("sheet-selectors");
    container.innerHTML = "";
    container.className = "weekly-plan-list";

    this.uploadedFiles.forEach((file, index) => {
      const div = document.createElement("div");

      let autoSelectedSheet = "";
      const exactMatch = file.sheets.find(
        (s) => s.trim() === "รวมงานผลิต - ราคาLCA00-LCD00",
      );
      const partialMatch = file.sheets.find(
        (s) => s.includes("รวมงานผลิต") && s.includes("ราคา"),
      );
      const generalMatch = file.sheets.find((s) => s.includes("รวมงานผลิต"));

      if (exactMatch) {
        autoSelectedSheet = exactMatch;
      } else if (partialMatch) {
        autoSelectedSheet = partialMatch;
      } else if (generalMatch) {
        autoSelectedSheet = generalMatch;
      } else if (file.sheets.length === 1) {
        autoSelectedSheet = file.sheets[0];
      }

      div.setAttribute(
        "class",
        `weekly-plan-row ${autoSelectedSheet ? "row-status-ready" : "row-status-pending"}`,
      );

      let sheetsOptions = "";
      file.sheets.forEach((sheet) => {
        const isSelected = sheet === autoSelectedSheet ? "selected" : "";
        sheetsOptions += `<option value="${sheet}" ${isSelected}>${sheet}</option>`;
      });

      div.innerHTML = `
        <span class="row-index">${String(index + 1).padStart(2, "0")}</span>
        <div class="row-file">
            <div class="row-file-line">
                <span class="row-status-dot"></span>
                <span class="row-file-name" title="${file.fileName}">${file.fileName}</span>
                <span class="row-sheet-count">${file.sheets.length} ชีต</span>
            </div>
            ${autoSelectedSheet ? "" : `<span class="row-pending-note">ยังไม่ได้เลือกชีต</span>`}
        </div>
        <select class="sheet-selector row-select" data-file-index="${index}">
            <option value="" ${autoSelectedSheet ? "" : "selected"}>เลือก Sheet ที่ต้องการประมวลผล</option>
            ${sheetsOptions}
        </select>
    `;

      container.appendChild(div);
    });
  },

  async processWeeklyPlan() {
    // Collect selected sheets
    const selectors = document.querySelectorAll(".sheet-selector");
    const filesData = [];

    let validationFailed = false;
    for (let index = 0; index < selectors.length; index++) {
      const select = selectors[index];
      const selectedSheet = select.value;
      if (!selectedSheet) {
        Swal.fire({
          title: "ข้อมูลไม่ครบถ้วน",
          text: `กรุณาเลือก Sheet สำหรับไฟล์ ${this.uploadedFiles[index].fileName}`,
          icon: "warning",
          confirmButtonText: "ตกลง",
          buttonsStyling: false,
          customClass: { popup: "p-6", confirmButton: "btn-custom-blue" },
        });
        validationFailed = true;
        break;
      }

      filesData.push({
        sessionId: this.uploadedFiles[index].sessionId,
        selectedSheet: selectedSheet,
      });
    }

    if (validationFailed) return;

    const processBtn = document.getElementById("btn-confirm-match");
    const backBtn = document.getElementById("btn-back-to-files");
    const closeBtn = document.getElementById("btn-close-upload-modal");
    const sheetSelectors = document.querySelectorAll(".sheet-selector");

    const setAllLocked = (locked) => {
      this.isProcessing = locked;

      // ปุ่มดำเนินการจับคู่
      if (processBtn) {
        processBtn.disabled = locked;
        if (locked) {
          processBtn.classList.add("opacity-80", "cursor-not-allowed");
        } else {
          processBtn.classList.remove("opacity-80", "cursor-not-allowed");
        }
      }

      // ปุ่มกลับไปเลือกไฟล์ใหม่
      if (backBtn) {
        backBtn.disabled = locked;
        if (locked) {
          backBtn.classList.add(
            "opacity-50",
            "cursor-not-allowed",
            "pointer-events-none",
          );
        } else {
          backBtn.classList.remove(
            "opacity-50",
            "cursor-not-allowed",
            "pointer-events-none",
          );
        }
      }

      // ปุ่มปิด X มุมขวาบน
      if (closeBtn) {
        closeBtn.disabled = locked;
        if (locked) {
          closeBtn.classList.add(
            "opacity-40",
            "cursor-not-allowed",
            "pointer-events-none",
          );
        } else {
          closeBtn.classList.remove(
            "opacity-40",
            "cursor-not-allowed",
            "pointer-events-none",
          );
        }
      }

      // ช่องเลือก Sheet
      sheetSelectors.forEach((sel) => {
        sel.disabled = locked;
        if (locked) {
          sel.classList.add("opacity-60", "cursor-not-allowed", "bg-slate-100");
        } else {
          sel.classList.remove(
            "opacity-60",
            "cursor-not-allowed",
            "bg-slate-100",
          );
        }
      });
    };

    // แสดง loading ทันทีที่กดปุ่ม เพื่อให้ UX ตอบสนองฉับไว ไม่รู้สึกค้างหรือรอ
    setAllLocked(true);

    // Check for duplicates first before processing
    try {
      const checkRes = await fetch("/FileMerge/CheckDuplicateWeeklyPlans", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          reportName: this.reportName,
          files: filesData,
        }),
      });

      const checkData = await checkRes.json();

      if (
        checkData.success &&
        checkData.duplicates &&
        checkData.duplicates.length > 0
      ) {
        // คืนสถานะปุ่มก่อนเปิด Popup เตือนซ้ำ
        setAllLocked(false);

        let listHtml = '<div class="registry-list" style="max-height:260px;">';
        checkData.duplicates.forEach((item, i) => {
          let fileName = item;
          let sheetName = "";
          const match = item.match(/^(.*?)\s*\((?:ชีท|ชีต):\s*(.*?)\)$/);
          if (match) {
            fileName = match[1];
            sheetName = match[2];
          }
          listHtml += `
            <div class="registry-row">
              <span class="registry-index">${String(i + 1).padStart(2, "0")}</span>
              <span class="registry-dot dot-amber"></span>
              <div class="registry-main">
                <span class="registry-title" title="${fileName}">${fileName}</span>
                ${sheetName ? `<span class="registry-sub" title="${sheetName}">${sheetName}</span>` : ""}
              </div>
            </div>`;
        });
        listHtml += "</div>";

        const confirmResult = await Swal.fire({
          html: `
            <div class="popup-header">
              <svg class="popup-header-icon icon-amber" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
                <path stroke-linecap="round" stroke-linejoin="round" d="M12 9v3.75m9.303 3.376c.866 1.5-.217 3.374-1.948 3.374H4.645c-1.73 0-2.813-1.874-1.948-3.374L10.052 3.37c.866-1.5 3.032-1.5 3.898 0l7.354 12.752ZM12 15.75h.007v.008H12v-.008Z"/>
              </svg>
              <p class="popup-title">พบข้อมูลซ้ำในระบบ</p>
            </div>
            <p class="popup-subtitle">ระบบตรวจพบว่าข้อมูลของไฟล์และชีตต่อไปนี้ถูกบันทึกไว้ในระบบแล้ว คุณต้องการเขียนทับข้อมูลเดิมหรือไม่</p>
            ${listHtml}
          `,
          showCancelButton: true,
          reverseButtons: true,
          buttonsStyling: false,
          confirmButtonText: "ยืนยันเขียนทับ",
          cancelButtonText: "ยกเลิก",
          customClass: {
            popup: "p-6",
            confirmButton: "btn-custom-blue",
            cancelButton: "btn-custom-white",
            actions: "popup-footer",
          },
          width: "480px",
        });

        if (!confirmResult.isConfirmed) {
          return; // ยกเลิกการประมวลผล
        }
        setAllLocked(true);
      } else if (!checkData.success) {
        setAllLocked(false);
        Swal.fire("เกิดข้อผิดพลาด", checkData.error, "error");
        return;
      }
    } catch (error) {
      console.error("Check duplicate error:", error);
      setAllLocked(false);
      Swal.fire("เกิดข้อผิดพลาด", "ไม่สามารถตรวจสอบข้อมูลซ้ำซ้อนได้", "error");
      return;
    }

    // Show loading
    Swal.fire({
      title: "กำลังประมวลผล...",
      html: "กำลังจับคู่ข้อมูล กรุณารอสักครู่",
      allowOutsideClick: false,
      didOpen: () => {
        Swal.showLoading();
      },
    });

    try {
      const response = await fetch("/FileMerge/ProcessWeeklyPlan", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          reportName: this.reportName,
          files: filesData,
        }),
      });

      const data = await response.json();

      if (data.success) {
        // ปิดหน้าต่าง Popup นำเข้าไฟล์ (#upload-modal) ทันทีที่จับคู่สำเร็จ เพื่อไม่ให้ค้างอยู่ข้างหลังหน้าต่าง Swal "สำเร็จ!"
        this.isProcessing = false;
        this.closeModal();

        let totalMatched = data.fileResults.reduce(
          (sum, f) => sum + (f.matchedCount || 0),
          0,
        );
        let totalItems = data.fileResults.reduce(
          (sum, f) => sum + (f.totalCount || 0),
          0,
        );
        let totalRows =
          data.totalRowsProcessed ||
          data.fileResults.reduce(
            (sum, f) => sum + (f.totalRowsCount || f.totalCount || 0),
            0,
          );
        let matchPercentage =
          totalItems > 0 ? Math.round((totalMatched / totalItems) * 100) : 0;

        let resultHtml = `
          <div class="popup-header">
            <svg class="popup-header-icon icon-teal" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
              <path stroke-linecap="round" stroke-linejoin="round" d="M9 12.75 11.25 15 15 9.75M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z"/>
            </svg>
            <p class="popup-title">ประมวลผลเสร็จสิ้น</p>
          </div>
          <div style="display:flex;align-items:baseline;justify-content:space-between;padding:10px 2px 14px;border-bottom:1px solid #e2e8f0;margin:6px 0 14px;">
            <span style="font-size:12px;color:#64748b;line-height:1.5;">${data.message || `จับคู่สำเร็จทั้งหมด ${totalMatched} ใบสั่งผลิต`}<br><span style="font-size:11px;color:#94a3b8;">จากใบสั่งผลิตที่ไม่ซ้ำ ${totalItems} ใบ (บันทึกประวัติ ${totalRows} แถว)</span></span>
            <span style="font-family:monospace;font-size:16px;font-weight:700;color:#00288e;white-space:nowrap;">${totalMatched}<span style="color:#94a3b8;font-weight:400;font-family:'IBM Plex Sans Thai',sans-serif;font-size:11px;"> / ${totalItems} ใบ (${matchPercentage}%)</span></span>
          </div>
          <p style="font-size:11px;font-weight:700;color:#94a3b8;text-transform:uppercase;letter-spacing:.02em;margin:0 2px 6px;text-align:left;">รายการชีตที่ประมวลผล (${data.fileResults.length} แผ่นงาน)</p>
        `;

        resultHtml += '<div class="registry-list" style="max-height:280px;">';
        data.fileResults.forEach((file, i) => {
          const matched = file.matchedCount || 0;
          const total = file.totalCount || 0;
          const rowsCount = file.totalRowsCount || total;
          const dotClass = matched > 0 ? "dot-teal" : "dot-neutral";
          const metaClass =
            matched > 0 ? "registry-meta meta-teal" : "registry-meta";
          resultHtml += `
            <div class="registry-row" title="พบข้อมูลใน Excel จำนวน ${rowsCount} แถว">
              <span class="registry-index">${String(i + 1).padStart(2, "0")}</span>
              <span class="registry-dot ${dotClass}"></span>
              <div class="registry-main">
                <span class="registry-title" title="${file.fileName}">${file.fileName}</span>
                <span class="registry-sub" title="${file.sheetName}">${file.sheetName} (${rowsCount} แถว)</span>
              </div>
              <span class="${metaClass}">${matched}/${total} ใบ</span>
            </div>`;
        });
        resultHtml += "</div>";

        Swal.fire({
          html: resultHtml,
          confirmButtonText: "ตกลง",
          buttonsStyling: false,
          customClass: {
            popup: "p-6",
            confirmButton: "btn-custom-blue",
            actions: "popup-footer",
          },
          width: "480px",
        }).then(() => {
          this.closeModal();
          window.location.reload();
        });
      } else {
        Swal.fire("เกิดข้อผิดพลาด", data.error, "error");
      }
    } catch (error) {
      console.error("Process error:", error);
      Swal.fire("เกิดข้อผิดพลาด", "ไม่สามารถประมวลผลได้", "error");
    } finally {
      this.isProcessing = false;
      const processBtn = document.getElementById("btn-confirm-match");
      const backBtn = document.getElementById("btn-back-to-files");
      const closeBtn = document.getElementById("btn-close-upload-modal");
      if (processBtn) {
        processBtn.disabled = false;
        processBtn.innerHTML = "ดำเนินการจับคู่";
        processBtn.classList.remove("opacity-80", "cursor-not-allowed");
      }
      if (backBtn) {
        backBtn.disabled = false;
        backBtn.classList.remove(
          "opacity-50",
          "cursor-not-allowed",
          "pointer-events-none",
        );
      }
      if (closeBtn) {
        closeBtn.disabled = false;
        closeBtn.classList.remove(
          "opacity-40",
          "cursor-not-allowed",
          "pointer-events-none",
        );
      }
      document.querySelectorAll(".sheet-selector").forEach((sel) => {
        sel.disabled = false;
        sel.classList.remove(
          "opacity-60",
          "cursor-not-allowed",
          "bg-slate-100",
        );
      });
    }
  },

  closeModal() {
    if (this.isProcessing) return;
    const modal = document.getElementById("upload-modal");
    if (modal) {
      modal.classList.add("hidden");
    }
    this.backToUpload();
  },

  backToUpload() {
    if (this.isProcessing) return;
    document.getElementById("sheet-selection-step").classList.add("hidden");
    document.getElementById("upload-step").classList.remove("hidden");
    document.getElementById("weeklyPlanFiles").value = "";
    document.getElementById("files-list").innerHTML = "";
    document.getElementById("btn-upload-files").classList.add("hidden");
    this.uploadedFiles = [];

    // Contract modal width back to normal
    const modalContainer = document.getElementById("upload-modal-container");
    if (modalContainer) {
      modalContainer.classList.remove("max-w-4xl", "modal-container-expanded");
      modalContainer.classList.add("max-w-lg");
    }
  },
};
