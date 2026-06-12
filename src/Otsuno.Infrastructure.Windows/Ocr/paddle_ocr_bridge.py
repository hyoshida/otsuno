import json
import sys


ocr_cache = {}


def write_response(response):
    print(json.dumps(response, ensure_ascii=False), flush=True)


def create_ocr(language):
    from paddleocr import PaddleOCR

    constructors = [
        lambda: PaddleOCR(lang=language, use_angle_cls=True, use_gpu=False, show_log=False),
        lambda: PaddleOCR(lang=language, use_angle_cls=True, show_log=False),
        lambda: PaddleOCR(lang=language),
    ]
    last_error = None
    for constructor in constructors:
        try:
            return constructor()
        except Exception as error:
            last_error = error

    raise last_error


def get_ocr(language):
    if language not in ocr_cache:
        ocr_cache[language] = create_ocr(language)
    return ocr_cache[language]


def recognize(image_path, language):
    ocr = get_ocr(language)
    if hasattr(ocr, "ocr"):
        return parse_legacy_result(ocr.ocr(image_path, cls=True))

    if hasattr(ocr, "predict"):
        return parse_predict_result(ocr.predict(image_path))

    raise RuntimeError("Unsupported PaddleOCR API.")


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
    texts = data.get("rec_texts") or data.get("texts") or []
    scores = data.get("rec_scores") or data.get("scores") or []
    boxes = data.get("rec_polys") or data.get("dt_polys") or data.get("boxes") or []
    for index, text in enumerate(texts):
        if index >= len(boxes):
            continue
        score = float(scores[index]) if index < len(scores) else 1.0
        add_region(regions, boxes[index], text, score)


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
    if not box:
        return []

    if isinstance(box, dict):
        box = box.get("points") or box.get("box") or []

    if len(box) == 4 and all(isinstance(value, (int, float)) for value in box):
        left, top, right, bottom = box
        return [(left, top), (right, top), (right, bottom), (left, bottom)]

    points = []
    for point in box:
        if isinstance(point, (list, tuple)) and len(point) >= 2:
            points.append((float(point[0]), float(point[1])))

    return points


def main():
    for line in sys.stdin:
        try:
            request = json.loads(line)
            regions = recognize(request["ImagePath"], request["Language"])
            write_response({"Regions": regions})
        except Exception as error:
            write_response({"Regions": [], "Error": str(error)})


if __name__ == "__main__":
    main()
