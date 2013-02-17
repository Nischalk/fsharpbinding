(require 'test-common)

(defvar test-file-dir (concat (file-name-directory (or load-file-name (buffer-file-name)))
                              "Test1/")
  "The directory contains F# files for testing.")

;;; ----------------------------------------------------------------------------

;;; Jump to defn

(defconst finddeclstr1
  (let ((file (concat test-file-dir "Program.fs")))
    (format "DATA: finddecl\nfile stored in metadata is '%s'\n%s:1:6\n<<EOF>>\n" file file))
  "A message for jumping to a definition in the same file")

(defconst finddeclstr2
  (let ((file (concat test-file-dir "FileTwo.fs")))
    (format "DATA: finddecl\nfile stored in metadata is '%s'\n%s:12:11\n<<EOF>>\n" file file))
    "A message for jumping to a definition in the another file")

(check "jumping to local definition should not change buffer"
  (let ((f (concat test-file-dir "Program.fs")))
    (using-file f
      (load-fsharp-mode)
      (ac-fsharp-filter-output nil finddeclstr1)
      (should (equal f (buffer-file-name))))))

(check "jumping to local definition should move point to definition"
  (using-file (concat test-file-dir "Program.fs")
    (load-fsharp-mode)
    (ac-fsharp-filter-output nil finddeclstr1)
    (should (equal (point) 18))))

(check "jumping to definition in another file should open that file"
  (let ((f1 (concat test-file-dir "Program.fs"))
        (f2 (concat test-file-dir "FileTwo.fs")))
    (using-file f1
      (load-fsharp-mode)
      (ac-fsharp-filter-output nil finddeclstr2)
      (should (equal (buffer-file-name) f2)))))

(check "jumping to definition in another file should move point to definition"
  (using-file (concat test-file-dir "Program.fs")
    (load-fsharp-mode)
    (ac-fsharp-filter-output nil finddeclstr2)
    (should (equal (point) 127))))

;;; Error parsing

(defconst err-brace-str
  (mapconcat
     'identity
     '("DATA: errors"
       "[9:0-9:2] WARNING Possible incorrect indentation: this token is offside of context started at position (2:16)."
       "Try indenting this token further or using standard formatting conventions."
       "[11:0-11:2] ERROR Unexpected symbol '[<' in expression"
       "Followed by more stuff on this line"
       "[12:0-12:3] WARNING Possible incorrect indentation: this token is offside of context started at position (2:16).
Try indenting this token further or using standard formatting conventions."
       "<<EOF>>"
       "")
     "\n")
  "A list of errors containing a square bracket to check the parsing")

(defmacro testing-error-handling (&rest body)
  "Run BODY forms within the context of the fsharp-mode-wrapper function."
  (declare (indent 1))
  `(fsharp-mode-wrapper
    '("Program.fs")
    (lambda ()
      (find-file (concat test-file-dir "Program.fs"))
      (ac-fsharp-filter-output nil err-brace-str)
      ,@body)))

(check "error clears partial data"
  (testing-error-handling
      (should (equal "" ac-fsharp-partial-data))))

(check "errors cause overlays to be drawn"
  (testing-error-handling
      (should (equal 3 (length (overlays-in (point-min) (point-max)))))))

(check "error overlay has expected text"
  (testing-error-handling
      (let* ((ov (overlays-in (point-min) (point-max)))
             (text (overlay-get (car-safe ov) 'help-echo)))
        (should (equal text (concat "Possible incorrect indentation: this token is offside of context started at position (2:16)."
                                    "\nTry indenting this token further or using standard formatting conventions."))))))

(check "first overlay should have the warning face"
  (testing-error-handling
      (let* ((ov (overlays-in (point-min) (point-max)))
             (face (overlay-get (car ov) 'face)))
        (should (eq 'fsharp-warning-face face)))))

(check "second overlay should have the error face"
  (testing-error-handling
      (let* ((ov (overlays-in (point-min) (point-max)))
             (face (overlay-get (cadr ov) 'face)))
        (should (eq 'fsharp-error-face face)))))