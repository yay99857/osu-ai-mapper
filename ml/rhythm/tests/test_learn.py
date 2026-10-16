from osu_ai_rhythm.learn import comment, score


def summary(f1=0.7, acc=0.9, gap=0.4, slider=0.1, model=2.2, human=2.0):
    return {"clickF1": f1, "typeAtHumanHeads": {"accuracy": acc},
            "endToEnd": {"gapHistogramL1": gap, "sliderShare": {"meanAbsError": slider}, "densityLoudOverQuiet": {"model": model, "human": human}}}


def test_a_better_map_scores_higher_and_the_comment_says_why():
    s0, p0 = score(summary())
    s1, p1 = score(summary(f1=0.75, slider=0.05))
    assert s1 > s0
    text = comment(p0, p1, "mapper:Sotarks", 50)
    assert "Sotarks" in text and "onde os humanos clicam" in text and "sliders" in text


def test_no_change_is_said_plainly():
    _, p = score(summary())
    assert "Nada mudou" in comment(p, p, "all", 10)


def test_change_is_printed_with_three_integer_digits():
    assert f"{0.5437:+07.2f}%" == "+000.54%"
    assert f"{-2.5:+07.2f}%" == "-002.50%"
