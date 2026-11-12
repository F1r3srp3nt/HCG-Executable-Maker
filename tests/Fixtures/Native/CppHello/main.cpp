#include <iostream>
#include <optional>
#include <string>

static std::optional<std::string> greeting(bool polite) {
    if (polite) return std::string("Hello from C++17");
    return std::nullopt;
}

int main() {
    std::cout << greeting(true).value_or("?") << std::endl;
    return 0;
}
