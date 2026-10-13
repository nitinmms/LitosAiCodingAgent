package com.acme;

public final class Calc {
    public static int add(int a, int b) {
        return a + b;
    }

    public static int scale(int value, int factor) {
        if (factor == 0) {
            throw new IllegalArgumentException("factor must not be zero");
        }
        return value * factor;
    }
}
