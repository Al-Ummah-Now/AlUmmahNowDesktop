#!/usr/bin/env python3
"""Draws the demo title-card thumbnails for review frames and store screenshots.

One card per demo video and language: tests/fixtures/viewer-thumbs/thumb-N.jpg (English) and thumb-N.ar.jpg
(Arabic) for the viewer's DemoCatalogClient, and tests/fixtures/studio-thumbs/ the same for Studio's DemoTransport.
A card shows only its own language (title, gold rule, category, channel), so an Arabic frame never carries English.

Usage (macOS, Google Chrome for Arabic shaping, sips for JPEG): scripts/title-cards.py
The lists below must match DemoCatalogClient.Fixtures/Channels and DemoTransport's titles (same order).
"""
import html
import json
import pathlib
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"

VIEWER = [  # (category, English, Arabic, channel): DemoCatalogClient.Fixtures
    ("quran_recitation", "Surah Al-Mulk, full recitation", "سورة الملك كاملة", 1),
    ("quran_recitation", "Surah Ar-Rahman", "سورة الرحمن", 2),
    ("quran_tafsir", "Reflections on Surah Al-Kahf, part 1", "تأملات في سورة الكهف، الجزء الأول", 0),
    ("quran_tafsir", "The meaning of Al-Fatiha", "معاني سورة الفاتحة", 0),
    ("hadith_studies", "Hadith of the week: intentions", "حديث الأسبوع: النيات", 1),
    ("hadith_studies", "Forty hadith, episode 2", "الأربعون النووية، الحلقة الثانية", 2),
    ("fiqh", "Q&A: prayer while travelling", "سؤال وجواب: الصلاة في السفر", 2),
    ("fiqh", "Fasting: common questions", "الصيام: أسئلة شائعة", 0),
    ("islamic_history", "Seerah: the early years in Makkah", "السيرة: السنوات الأولى في مكة", 3),
    ("islamic_history", "Al-Andalus: a short history", "الأندلس: تاريخ موجز", 3),
    ("islamic_nasheed", "Tala'a al-Badru 'Alayna", "طلع البدر علينا", 1),
    ("islamic_nasheed", "A nasheed for the morning", "نشيد الصباح", 0),
    ("ramadan", "Preparing for Ramadan", "الاستعداد لرمضان", 1),
    ("ramadan", "Iftar around the world", "الإفطار حول العالم", 3),
    ("quran_recitation", "Surah Yasin, full recitation", "سورة يس كاملة", 2),
    ("quran_recitation", "Surah Al-Waqi'ah", "سورة الواقعة", 1),
    ("quran_tafsir", "Reflections on Surah Yusuf", "تأملات في سورة يوسف", 3),
    ("quran_tafsir", "Ayat al-Kursi explained", "شرح آية الكرسي", 1),
    ("hadith_studies", "Hadith of the week: mercy", "حديث الأسبوع: الرحمة", 0),
    ("hadith_studies", "Forty hadith, episode 3", "الأربعون النووية، الحلقة الثالثة", 2),
    ("fiqh", "Zakat: who pays and how", "الزكاة: على من تجب وكيف تُخرج", 0),
    ("fiqh", "Wudu step by step", "الوضوء خطوة بخطوة", 1),
    ("islamic_history", "The House of Wisdom in Baghdad", "بيت الحكمة في بغداد", 3),
    ("islamic_history", "Salah al-Din and Jerusalem", "صلاح الدين والقدس", 3),
    ("islamic_nasheed", "A nasheed for the evening", "نشيد المساء", 0),
    ("islamic_nasheed", "Mawlay salli wa sallim", "مولاي صلّ وسلّم", 2),
    ("ramadan", "Laylat al-Qadr reminders", "تذكير بليلة القدر", 0),
    ("ramadan", "Suhoor ideas for busy days", "أفكار للسحور في الأيام المزدحمة", 2),
]
VIEWER_CHANNELS = [("Al Ummah Now", "الأمة الآن"), ("Noor Channel", "قناة النور"), ("Masjid Live", "المسجد مباشر"), ("Seerah Stories", "قصص السيرة")]

STUDIO = [  # (category, English, Arabic): DemoTransport's videos, in order
    ("quran_tafsir", "Reflections on Surah Al-Kahf, part one", "تأملات في سورة الكهف — الجزء الأول"),
    ("islamic_history", "Seerah: the early years in Makkah", "السيرة: السنوات الأولى في مكة"),
    ("fiqh", "Fasting rulings for beginners", "أحكام الصيام للمبتدئين"),
    ("islamic_lectures", "Arabic grammar, part 3", "النحو العربي، الجزء الثالث"),
    ("khutbah", "Friday khutbah: patience", "خطبة الجمعة: الصبر"),
    ("hadith_studies", "Hadith of the week: intentions", "حديث الأسبوع: النيات"),
    ("islamic_history", "Stories of the prophets: Yunus", "قصص الأنبياء: يونس عليه السلام"),
    ("fiqh", "Q&A: prayer while travelling", "سؤال وجواب: الصلاة في السفر"),
]
STUDIO_CHANNEL = ("قناة النور", "قناة النور")  # the demo channel's own name (get_ChannelAccessList)

# Fawanees night, teal, green, wine and ink grounds; every card keeps the lantern-gold rule and mark.
GROUNDS = {
    "quran_recitation": ("#0b2233", "#1d5566"),
    "quran_tafsir": ("#14301f", "#3f7a52"),
    "hadith_studies": ("#2a1a12", "#7a4a26"),
    "fiqh": ("#101c3a", "#35508f"),
    "islamic_history": ("#2b1420", "#7a3550"),
    "islamic_nasheed": ("#1a1636", "#4d3f8a"),
    "ramadan": ("#0d2630", "#2f6f73"),
    "khutbah": ("#22201a", "#6b5a2c"),
    "islamic_lectures": ("#16243a", "#3d5f7a"),
}

CARD = """<!doctype html><html lang="{lang}" dir="{dir}"><meta charset="utf-8"><style>
html,body{{margin:0;width:1280px;height:720px;overflow:hidden}}
body{{background:radial-gradient(120% 140% at {glow} 0%,{b} 0%,{a} 70%);font-family:{font};color:#f6ecd6;
  display:flex;flex-direction:column;justify-content:center;align-items:center;text-align:center;position:relative}}
.lamp{{position:absolute;top:0;{start}:120px;width:4px;height:96px;background:linear-gradient(#e9b54b00,#e9b54b)}}
.lamp::after{{content:"";position:absolute;bottom:-34px;left:-26px;width:56px;height:56px;border-radius:50%;
  background:radial-gradient(circle at 40% 35%,#ffe9a8,#e9a63a 60%,#b9761e);box-shadow:0 0 60px 18px #e9b54b55}}
h1 span{{white-space:nowrap}}
h1{{font-size:{size}px;line-height:1.25;margin:0 120px;font-weight:700;text-shadow:0 4px 18px #0008}}
.rule{{width:150px;height:6px;border-radius:3px;background:#e9b54b;margin:34px 0 26px}}
.cat{{font-size:40px;color:#f0c66e;font-weight:600}}
.mark{{position:absolute;bottom:84px;{start}:52px;display:flex;align-items:center;gap:18px;font-size:34px;font-weight:600}}
.mark i{{width:36px;height:36px;border-radius:50%;background:radial-gradient(circle at 35% 35%,#ffe3a0,#e9a63a 65%,#b9761e)}}
</style><div class="lamp"></div><h1>{title}</h1><div class="rule"></div><div class="cat">{cat}</div>
<div class="mark"><i></i><span>{channel}</span></div></html>"""


def labels(lang):
    data = json.loads((ROOT / "src/AlUmmahNow.App/I18n" / f"{lang}.json").read_text(encoding="utf-8"))
    return {k.removeprefix("studio.category."): v for k, v in data.items() if k.startswith("studio.category.")}


def draw(out, lang, title, category, channel, work):
    arabic = lang == "ar"
    a, b = GROUNDS[category]
    page = work / (out.stem + ".html")
    page.write_text(CARD.format(
        lang=lang, dir="rtl" if arabic else "ltr", a=a, b=b, glow="85%" if arabic else "15%",
        start="right" if arabic else "left", end="left" if arabic else "right",
        font="'Noto Sans Arabic','SF Arabic','Geeza Pro',sans-serif" if arabic else "'Noto Sans','Inter','Helvetica Neue',sans-serif",
        size=88 if len(title) <= 26 else 74 if len(title) <= 36 else 64,
        title=" ".join(f"<span>{html.escape(w)}</span>" for w in title.split()), cat=html.escape(labels(lang)[category]), channel=html.escape(channel)), encoding="utf-8")
    png = work / (out.stem + ".png")
    subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
                    "--window-size=1280,720", f"--screenshot={png}", page.as_uri()], check=True, capture_output=True)
    subprocess.run(["sips", "-s", "format", "jpeg", "-s", "formatOptions", "82", str(png), "--out", str(out)], check=True, capture_output=True)


def main():
    with tempfile.TemporaryDirectory() as tmp:
        work = pathlib.Path(tmp)
        viewer = ROOT / "tests/fixtures/viewer-thumbs"
        studio = ROOT / "tests/fixtures/studio-thumbs"
        studio.mkdir(parents=True, exist_ok=True)
        for i, (category, en, ar, owner) in enumerate(VIEWER):
            channel = VIEWER_CHANNELS[owner]
            draw(viewer / f"thumb-{i + 1}.jpg", "en", en, category, channel[0], work)
            draw(viewer / f"thumb-{i + 1}.ar.jpg", "ar", ar, category, channel[1], work)
        for i, (category, en, ar) in enumerate(STUDIO):
            draw(studio / f"thumb-{i + 1}.jpg", "en", en, category, STUDIO_CHANNEL[0], work)
            draw(studio / f"thumb-{i + 1}.ar.jpg", "ar", ar, category, STUDIO_CHANNEL[1], work)
    return 0


if __name__ == "__main__":
    sys.exit(main())
