def add(a, b):
    return a + b


def scale(value, factor):
    if factor == 0:
        raise ValueError("factor must not be zero")
    return value * factor
