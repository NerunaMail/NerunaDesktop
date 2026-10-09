"""
Browser tests for src/Client/Neruna.Desktop/Editor/editor.html in the engines behind the desktop WebView:
Chromium (= WebView2 on Windows) and WebKit (= macOS / Linux).

    docker run --rm -v "$PWD":/work -w /work mcr.microsoft.com/playwright/python:v1.55.0-noble \
        sh -c "pip install -q pytest pytest-playwright playwright==1.55.0 && pytest -q tests/editor"
"""
import pathlib
import pytest

EDITOR = pathlib.Path(__file__).resolve().parents[2] / "src/Client/Neruna.Desktop/Editor/editor.html"


@pytest.fixture(params=["chromium", "webkit"])
def page(request, playwright):
    browser = getattr(playwright, request.param).launch()
    page = browser.new_page()
    page.add_init_script("window.__nerunaMessages = []; window.__nerunaTestSink = m => window.__nerunaMessages.push(m);")
    page.goto(EDITOR.as_uri())
    page.evaluate("neruna.setContent('', 'Arial', 11, 'Nachricht schreiben')")
    yield page
    browser.close()


def last_state(page):
    page.wait_for_timeout(120)
    return page.evaluate("window.__nerunaMessages.filter(m => m.type === 'state').slice(-1)[0]")


def select_all(page):
    page.evaluate("""() => { const r = document.createRange(); r.selectNodeContents(document.getElementById('editor'));
                     const s = getSelection(); s.removeAllRanges(); s.addRange(r); }""")


def test_ready_and_default_font_is_exported(page):
    page.keyboard.type("Hallo Anna")
    html = page.evaluate("neruna.getHtml()")
    assert "font-family: Arial" in html and "font-size: 11pt" in html
    assert "Hallo Anna" in html
    assert page.evaluate("window.__nerunaMessages.some(m => m.type === 'ready')")


def test_bold_italic_underline_strikethrough(page):
    page.keyboard.type("Wichtig")
    select_all(page)
    for command in ["bold", "italic", "underline", "strikeThrough"]:
        page.evaluate(f"neruna.exec('{command}')")
    state = last_state(page)
    assert state["bold"] and state["italic"] and state["underline"] and state["strikethrough"]
    html = page.evaluate("neruna.getHtml()").lower()
    assert "bold" in html or "<b>" in html
    assert "italic" in html or "<i>" in html
    assert "underline" in html or "<u>" in html
    assert "line-through" in html or "<strike>" in html or "<s>" in html


def test_font_family_size_and_color_on_selection(page):
    page.keyboard.type("Text in Georgia")
    select_all(page)
    page.evaluate("neruna.font('Georgia')")
    page.evaluate("neruna.size(18)")
    page.evaluate("neruna.color('#c50f1f')")
    state = last_state(page)
    assert state["font"] == "Georgia"
    assert state["size"] == 18
    assert state["color"] == "#c50f1f"
    html = page.evaluate("neruna.getHtml()")
    assert "18pt" in html and "Georgia" in html
    assert "xx-large" not in html


def test_size_at_caret_applies_to_typed_text(page):
    page.keyboard.type("klein ")
    page.evaluate("neruna.size(24)")
    page.keyboard.type("GROSS")
    html = page.evaluate("neruna.getHtml()")
    assert "font-size: 24pt" in html and "GROSS" in html
    assert "​" not in html
    assert last_state(page)["size"] == 24


def test_lists_and_state(page):
    page.keyboard.type("Punkt")
    page.evaluate("neruna.exec('insertUnorderedList')")
    assert last_state(page)["bulletList"]
    assert "<ul>" in page.evaluate("neruna.getHtml()")


def test_ctrl_enter_requests_send(page):
    page.keyboard.type("x")
    page.keyboard.press("Control+Enter")
    assert page.evaluate("window.__nerunaMessages.some(m => m.type === 'send')")


def test_quoted_html_keeps_formatting_and_cannot_run_scripts(page):
    page.evaluate("""neruna.setContent('<div><br></div><blockquote><b>Original</b><img src="x" onerror="window.__pwned=1"></blockquote>', 'Arial', 11, '')""")
    page.wait_for_timeout(200)
    assert page.evaluate("window.__pwned === undefined")
    assert "<b>Original</b>" in page.evaluate("neruna.getHtml()")


def test_empty_detection(page):
    assert page.evaluate("neruna.isEmpty()")
    page.keyboard.type("a")
    assert not page.evaluate("neruna.isEmpty()")


def test_signature_is_replaced_above_the_quote_not_in_it(page):
    page.evaluate("""neruna.setContent('<div><br></div><div><br></div><div id="neruna-signature"><b>Anna</b></div><div><br></div><div>Von: Lea</div><div>Zitat</div>', 'Arial', 11, '')""")
    page.keyboard.type("Hallo Lea")
    page.evaluate("neruna.setSignature('<i>Anna Muster</i><br>Example AG')")
    html = page.evaluate("neruna.getHtml()")
    assert "<i>Anna Muster</i><br>Example AG" in html
    assert "<b>Anna</b>" not in html
    assert html.index("Hallo Lea") < html.index("Anna Muster") < html.index("Zitat")

    page.evaluate("neruna.setSignature('')")
    html = page.evaluate("neruna.getHtml()")
    assert "Anna Muster" not in html and "Zitat" in html and "Hallo Lea" in html


def test_signature_container_is_created_when_missing(page):
    page.evaluate("neruna.setContent('<div>Text</div>', 'Arial', 11, '')")
    page.evaluate("neruna.setSignature('Gruss')")
    assert page.evaluate("document.querySelector('#neruna-signature').textContent") == "Gruss"


def test_insert_image(page):
    page.evaluate("neruna.setContent('', 'Arial', 11, '')")
    page.evaluate("neruna.insertImage('data:image/png;base64,iVBORw0KGgo=')")
    assert 'src="data:image/png;base64,iVBORw0KGgo="' in page.evaluate("neruna.getHtml()")


def test_body_html_has_no_font_wrapper(page):
    page.keyboard.type("Anna Muster")
    body = page.evaluate("neruna.getBodyHtml()")
    assert "Anna Muster" in body and "font-family" not in body


def test_a_new_paragraph_stands_apart_from_a_wrapped_line(page):
    # (Typing into the empty editor must already create a paragraph block, not loose text.)
    # A line long enough to wrap, Enter, then a second paragraph.
    page.evaluate("document.getElementById('editor').style.width = '300px'")
    page.click("#editor")
    page.keyboard.type("Diese Zeile ist so lang, dass sie im schmalen Editor automatisch umbricht und weiterläuft.")
    page.keyboard.press("Enter")
    page.keyboard.type("Zweiter Absatz")
    gaps = page.evaluate("""() => {
        const blocks = [...document.getElementById('editor').children];
        const r1 = document.createRange(); r1.selectNodeContents(blocks[0]);
        const lines = [...r1.getClientRects()];
        const tops = [...new Set(lines.map(l => Math.round(l.top)))].sort((a, b) => a - b);
        const lineStep = tops[1] - tops[0];
        const r2 = document.createRange(); r2.selectNodeContents(blocks[1]);
        const paragraphStep = Math.round(r2.getClientRects()[0].top) - tops[tops.length - 1];
        return { lineStep, paragraphStep };
    }""")
    assert gaps["paragraphStep"] > gaps["lineStep"] * 1.3, gaps

    # Recipients see the same spacing: it is in the exported HTML, on the paragraphs only.
    html = page.evaluate("neruna.getBodyHtml()")
    assert html.count("margin: 0px 0px 0.5em") == 2 or html.count("margin: 0 0 0.5em") == 2, html


def test_dark_theme_inverts_the_page_but_not_the_mail(page):
    page.click("#editor")
    page.keyboard.type("Hallo")
    page.evaluate("neruna.setDark(true)")
    assert "invert" in page.evaluate("getComputedStyle(document.documentElement).filter")
    html = page.evaluate("neruna.getHtml()")
    assert "color: #000000" in html and "invert" not in html
    page.evaluate("neruna.setDark(false)")
    assert page.evaluate("getComputedStyle(document.documentElement).filter") == "none"


def test_a_text_template_goes_to_the_last_caret_position(page):
    page.click("#editor")
    page.keyboard.type("Hallo Welt")
    for _ in range(4):
        page.keyboard.press("ArrowLeft")
    # Focus leaves the page (the template menu); the caret position is remembered.
    page.evaluate("document.activeElement.blur()")
    page.evaluate("neruna.insertHtml('<b>schöne </b>')")
    text = page.evaluate("document.getElementById('editor').textContent").replace("\u200b", "").replace("\xa0", " ")
    # Exactly between "Hallo " and "Welt" (trailing spaces of inserted HTML are the browser's business).
    assert text.startswith("Hallo schöne") and text.endswith("Welt"), text


def test_a_shortcut_with_two_colons_becomes_the_template(page):
    page.evaluate("neruna.setShortcuts({'TEL': '<table><tr><td>Name</td><td></td></tr></table>'})")
    page.click("#editor")
    page.keyboard.type("Notiz: tel::")
    html = page.evaluate("neruna.getBodyHtml()")
    assert "<table" in html and "tel::" not in html, html
    assert "Notiz:" in html
    # Unknown shortcuts and words without "::" stay as typed.
    page.keyboard.type(" xy:: tel:")
    assert "xy:: tel:" in page.evaluate("document.getElementById('editor').textContent")


FORM = ('<p>Anruf</p><table><tbody>'
        '<tr><td><p>Name</p></td><td><p><br></p></td></tr>'
        '<tr><td><p>Tel</p></td><td><p><br></p></td></tr></tbody></table>')


def test_a_template_with_a_form_is_filled_in_with_tab(page):
    page.click("#editor")
    page.evaluate(f"neruna.insertHtml('{FORM}')")
    # The caret lands in the first empty cell; Tab moves on (to the end of a filled cell), Tab in the last cell adds a row.
    page.keyboard.type("Anna Muster")
    page.keyboard.press("Tab")
    page.keyboard.type("x")
    page.keyboard.press("Shift+Tab")
    page.keyboard.press("Tab")
    page.keyboard.press("Tab")
    page.keyboard.type("044 123 45 67")
    page.keyboard.press("Tab")
    page.keyboard.type("Firma")
    rows = page.evaluate("[...document.querySelectorAll('#editor tr')].map(r => [...r.cells].map(c => c.textContent.trim()))")
    assert rows == [["Name", "Anna Muster"], ["Telx", "044 123 45 67"], ["Firma", ""]], rows
