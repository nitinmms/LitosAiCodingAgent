package calc

import "errors"

func Add(a, b int) int {
	return a + b
}

func Scale(value, factor int) (int, error) {
	if factor == 0 {
		return 0, errors.New("factor must not be zero")
	}
	return value * factor, nil
}
