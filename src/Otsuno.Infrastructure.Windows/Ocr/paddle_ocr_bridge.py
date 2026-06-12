import json
import os
import sys
import time


os.environ.setdefault("PADDLE_PDX_ENABLE_MKLDNN_BYDEFAULT", "0")
os.environ.setdefault("FLAGS_use_mkldnn", "0")

ocr_cache = {}


def configure_io():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")


def write_status(message):
    print(f"[PaddleOCR bridge] {message}", file=sys.stderr, flush=True)


def write_response(response):
    print(json.dumps(response, ensure_ascii=True), flush=True)


def create_ocr(language):
    write_status(f"Importing paddleocr package for language '{language}'.")
    from paddleocr import PaddleOCR
    write_status(f"Imported paddleocr package for language '{language}'.")

    constructors = [
        lambda: PaddleOCR(lang=language, use_angle_cls=True, use_gpu=False, show_log=False),
        lambda: PaddleOCR(lang=language, use_angle_cls=True, show_log=False),
        lambda: PaddleOCR(lang=language),
    ]
    last_error = None
    for index, constructor in enumerate(constructors, start=1):
        try:
            write_status(f"Creating PaddleOCR instance for language '{language}' with constructor {index}/{len(constructors)}.")
            ocr = constructor()
            write_status(f"Created PaddleOCR instance for language '{language}'.")
            return ocr
        except Exception as error:
            write_status(f"PaddleOCR constructor {index}/{len(constructors)} failed for language '{language}': {error}")
            last_error = error

    raise last_error


def get_ocr(language):
    if language not in ocr_cache:
        write_status(f"Loading PaddleOCR model for language '{language}'.")
        ocr_cache[language] = create_ocr(language)
        write_status(f"PaddleOCR model is ready for language '{language}'.")
    return ocr_cache[language]


def recognize(image_path, language):
    started = time.perf_counter()
    write_status(f"Starting OCR for language '{language}' on '{os.path.basename(image_path)}'.")
    ocr = get_ocr(language)
    if hasattr(ocr, "predict"):
        regions = parse_predict_result(ocr.predict(image_path))
        write_status(f"Finished OCR with predict API for language '{language}': {len(regions)} regions in {time.perf_counter() - started:.2f}s.")
        return regions

    if hasattr(ocr, "ocr"):
        regions = parse_legacy_result(call_legacy_ocr(ocr, image_path))
        write_status(f"Finished OCR with legacy API for language '{language}': {len(regions)} regions in {time.perf_counter() - started:.2f}s.")
        return regions

    raise RuntimeError("Unsupported PaddleOCR API.")


def call_legacy_ocr(ocr, image_path):
    try:
        return ocr.ocr(image_path, cls=True)
    except TypeError as error:
        if "cls" not in str(error):
            raise
        return ocr.ocr(image_path)


def parse_legacy_result(result):
    regions = []
    pages = result if is_page_list(result) else [result]
    for page in pages:
        if page is None:
            continue
        for item in page:
            if not item or len(item) < 2:
                continue
            box = item[0]
            text_info = item[1]
            if not text_info or len(text_info) < 2:
                continue
            add_region(regions, box, text_info[0], float(text_info[1]))

    return regions


def is_page_list(result):
    return isinstance(result, list) and (not result or isinstance(result[0], list) and result[0] and isinstance(result[0][0], list))


def parse_predict_result(result):
    regions = []
    for page in result:
        data = page.json if hasattr(page, "json") else page
        if callable(data):
            data = data()
        if isinstance(data, dict):
            parse_predict_dict(regions, data)

    return regions


def parse_predict_dict(regions, data):
    nested = get_first_present(data, "res", "result", "data")
    if isinstance(nested, dict):
        parse_predict_dict(regions, nested)
        return

    texts = get_first_present(data, "rec_texts", "texts")
    scores = get_first_present(data, "rec_scores", "scores")
    boxes = get_first_present(data, "rec_polys", "dt_polys", "boxes")
    texts = [] if texts is None else texts
    scores = [] if scores is None else scores
    boxes = [] if boxes is None else boxes
    for index, text in enumerate(texts):
        if index >= len(boxes):
            continue
        score = float(scores[index]) if index < len(scores) else 1.0
        add_region(regions, boxes[index], text, score)


def get_first_present(data, *keys):
    for key in keys:
        if key in data and data[key] is not None:
            return data[key]

    return None


def add_region(regions, box, text, confidence):
    if not text or not str(text).strip():
        return

    points = normalize_points(box)
    if not points:
        return

    xs = [point[0] for point in points]
    ys = [point[1] for point in points]
    left = int(min(xs))
    top = int(min(ys))
    right = int(max(xs))
    bottom = int(max(ys))
    if right <= left or bottom <= top:
        return

    regions.append({
        "text": str(text).strip(),
        "x": left,
        "y": top,
        "width": right - left,
        "height": bottom - top,
        "confidence": confidence,
    })


def normalize_points(box):
    if box is None:
        return []

    if hasattr(box, "tolist"):
        box = box.tolist()

    if isinstance(box, dict):
        box = box.get("points") or box.get("box") or []

    if not box:
        return []

    if len(box) == 4 and all(isinstance(value, (int, float)) for value in box):
        left, top, right, bottom = box
        return [(left, top), (right, top), (right, bottom), (left, bottom)]

    points = []
    for point in box:
        if isinstance(point, (list, tuple)) and len(point) >= 2:
            points.append((float(point[0]), float(point[1])))

    return points


def main():
    configure_io()
    write_status("Bridge process is ready and waiting for OCR requests.")
    for line in sys.stdin:
        try:
            request = json.loads(line)
            regions = recognize(request["ImagePath"], request["Language"])
            write_response({"Regions": regions})
        except Exception as error:
            write_status(f"OCR request failed: {error}")
            write_response({"Regions": [], "Error": str(error)})


if __name__ == "__main__":
    main()
