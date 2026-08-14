window.litosChat = {
    scrollToBottom: function (element) {
        if (element) {
            element.scrollTo({ top: element.scrollHeight, behavior: 'smooth' });
        }
    }
};
